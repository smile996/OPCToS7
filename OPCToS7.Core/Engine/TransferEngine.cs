using OPCToS7.Core.Models;
using OPCToS7.Core.OpcUa;
using OPCToS7.Core.Siemens;
using OPCToS7.Storage.Entities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OPCToS7.Core.Engine;

public class TransferEngine
{
    // 记录每个点位最后一次确认的双向同步值
    private ConcurrentDictionary<string, object> _shadowCache = new();

    // 写入冷却锁：记录每个点位最后一次从 OPC 写入 PLC 的时间
    private ConcurrentDictionary<string, DateTime> _plcWriteCooldowns = new();

    // 冷却死区时间：在下发写入命令后的 500ms 内，无视该点位的任何 PLC 读取结果
    private readonly TimeSpan _writeCooldownDuration = TimeSpan.FromMilliseconds(500);


    public OpcUaClientManager GetOpcClientManager() => _opcUaClient; 
    private readonly OpcUaClientManager _opcUaClient = new();
    private readonly S7Driver _s7Driver = new();
    private readonly S7PackManager _packManager = new();

    // 高性能内存映射路由字典：OpcNodeId -> RuntimeTag
    private readonly ConcurrentDictionary<string, RuntimeTag> _tagRoutingTable = new();

    private CancellationTokenSource? _cts;
    private int _plcCycleTimeMs = 10; // 默认10ms高频转发

    // 统一向外层 WPF 界面抛出的状态与日志代理
    public event Action<bool>? OnOpcStatusChanged;
    public event Action<bool>? OnS7StatusChanged;
    public event Action<string>? OnEngineLog;

    public TransferEngine()
    {
        // 绑定底层驱动的事件，向上层透传
        _opcUaClient.OnConnectionStatusChanged += status => OnOpcStatusChanged?.Invoke(status);
        _opcUaClient.OnLogMessage += msg => OnEngineLog?.Invoke($"[OPC UA] {msg}");

        _s7Driver.OnConnectionStatusChanged += status => OnS7StatusChanged?.Invoke(status);
        _s7Driver.OnLogMessage += msg => OnEngineLog?.Invoke($"[S7 PLC] {msg}");

        // 绑定数据路由逻辑
        _opcUaClient.OnTagValueChanged += HandleOpcDataIncoming;
    }

    /// <summary>
    /// 启动网关核心转发引擎
    /// </summary>
    public async Task StartAsync(string opcUrl, string plcIp, int plcRack, int plcSlot, List<RuntimeTag> activeTags, int cycleTimeMs = 10)
    {
        _plcCycleTimeMs = cycleTimeMs;
        _cts = new CancellationTokenSource();

        
        // 防止网关热重启时，残留下一次的旧值和旧锁，导致数据被错误拦截
        _shadowCache?.Clear();
        _plcWriteCooldowns?.Clear();

        // 初始化并构建高性能常驻内存路由表
        _tagRoutingTable.Clear();
        foreach (var tag in activeTags)
        {
            // 尝试解析字符串类型为枚举类型 
            if (Enum.TryParse<S7DataType>(tag.DataType.ToUpper(), out var parsedType))
            {
                tag.S7Type = parsedType;
            }
            // 无条件装载激活的点位
            _tagRoutingTable[tag.OpcNodeId] = tag;
        }

        OnEngineLog?.Invoke($"网关引擎正在初始化... 成功映射 {_tagRoutingTable.Count} 个活跃点位。");

        // 启动西门子 S7 通讯长连接守护任务
        _s7Driver.Initialize(plcIp, plcRack, plcSlot);
        _ = _s7Driver.StartConnectLoopAsync(_cts.Token);

        //
        //
        // 异步建立 OPC UA 连接并开启变量订阅 (负责：OPC -> PLC 的正向触发)
        bool opcConnected = await _opcUaClient.ConnectAsync(opcUrl);
        if (opcConnected)
        {
            var nodesToSubscribe = _tagRoutingTable.Keys.ToList();
            _opcUaClient.SubscribeTags(nodesToSubscribe, publishingIntervalMs: _plcCycleTimeMs);
        }

        //开启独立的 S7 密集字节块批量异步写入循环 
        _ = Task.Run(() => StartS7FlusherLoopAsync(_cts.Token));

        // 开启 PLC 高频读取与防回传同步循环 (反向生产者线程：负责读 PLC 并回传 OPC)
        _ = Task.Run(() => StartPlcPollingLoopAsync(_cts.Token));

        OnEngineLog?.Invoke("网关中间件核心引擎已全线启动，进入全双工双向防抖数据调度状态。");
    }

    // 新增一个标签状态变动事件
    public event Action<string, string, object?>? OnTagStatusChanged; // 参数：NodeId, 状态文字, 实时数据



    private void HandleOpcDataIncoming(string nodeId, object rawValue)
    {
        try
        {
            if (rawValue is Opc.Ua.LocalizedText localizedText) rawValue = localizedText.Text;
            else if (rawValue is System.Xml.XmlElement xmlElement) rawValue = xmlElement.InnerText;

            rawValue = rawValue ?? "";

            if (_shadowCache.TryGetValue(nodeId, out var shadowVal) && Equals(shadowVal, rawValue))
            {
                return; 
            }

            _shadowCache[nodeId] = rawValue;
            _plcWriteCooldowns[nodeId] = DateTime.Now; // 打上时间戳,刚写过PLC，这半秒内别读它！

            // 路由与打包
            if (_tagRoutingTable.TryGetValue(nodeId, out var runtimeTag))
            {
                _packManager.UpdateTagValue(runtimeTag, rawValue);
            }

            OnTagStatusChanged?.Invoke(nodeId, "正常", rawValue);
        }
        catch (Exception ex)
        {
            OnTagStatusChanged?.Invoke(nodeId, "变量错误", "数据解析异常");
        }
    }

    public event Action<string, object>? OnLiveDataReceived;


    /// <summary>
    /// 消费者循环：将内存中密集打包好的大块字节，以极低的定时开销批量倾泻至 PLC 中
    /// </summary>
    private async Task StartS7FlusherLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_plcCycleTimeMs, token);

                if (!_s7Driver.IsConnected) continue;

                // 提取出当前内存中各个DB块拼装好、有变动的最小连续物理负载
                var activePayloads = _packManager.GetActivePayloads();

                foreach (var (cacheKey, dataBytes) in activePayloads)
                {
                    // 拆解缓存字典的 Key，获取绝对物理地址
                    var parts = cacheKey.Split('_');
                    if (parts.Length != 3) continue;

                    int dbNumber = int.Parse(parts[0].Replace("DB", ""));
                    int byteOffset = int.Parse(parts[1].Replace("Byte", ""));

                    // 调用支持指定“起始偏移量”的写入方法。
                    _s7Driver.WriteDbBlock(dbNumber, byteOffset, dataBytes);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Console.WriteLine($"[内核异动警告] 批量转发任务发生异常: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 生产者循环：定时从 PLC 吸取大块内存数据，拆解比对后，双向同步给 OPC
    /// </summary>
    private async Task StartPlcPollingLoopAsync(CancellationToken token)
    {

        // 1. 预先将变量按 DB 块分组，并计算好 DB块号 和 最大读取长度
        var optimizedDbGroups = _tagRoutingTable.Values
            .Where(t => int.TryParse(t.DbNumber.ToString(), out _)) // 过滤掉无效DB号
            .GroupBy(t => int.Parse(t.DbNumber!.ToString()))
            .Select(g => new
            {
                DbNumber = g.Key,
                Tags = g.ToList(), // 固化为 List，避免遍历时产生枚举器开销
                // 精确计算该 DB 块需要的最大物理读取长度
                ReadLength = g.Max(t => t.ByteOffset + (t.DataType?.ToUpper() == "STRING" ? 256 : 4))
            })
            .ToList();

        if (optimizedDbGroups.Count == 0) return; // 如果没有有效点位，直接退出任务

        // 2. 动态精准分配全局唯一的读取缓冲区
        int globalMaxBuffer = optimizedDbGroups.Max(g => g.ReadLength);
        byte[] readBuffer = new byte[globalMaxBuffer];

        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_plcCycleTimeMs, token);

                if (!_s7Driver.IsConnected) continue;

                // 直接遍历已缓存好的极速内存结构
                foreach (var group in optimizedDbGroups)
                {
                    // 物理读取：精准读取事先算好的 ReadLength，绝不浪费 1 字节网络带宽
                    bool readSuccess = _s7Driver.ReadDbBlock(group.DbNumber, 0, group.ReadLength, readBuffer);

                    if (!readSuccess) continue;

                    // 在内存中执行极速切片解包
                    foreach (var tag in group.Tags)
                    {
                        string nodeId = tag.OpcNodeId;

                        // ====== 冷却死区检查 ======
                        if (_plcWriteCooldowns.TryGetValue(nodeId, out var lastWriteTime))
                        {
                            if (DateTime.Now - lastWriteTime < _writeCooldownDuration) continue;
                        }

                        // ====== 数据解包 ======
                        object? plcRawValue = _packManager.ExtractTagValue(tag, readBuffer);
                        if (plcRawValue == null) continue;

                        // ====== 脏数据比较 ======
                        if (_shadowCache.TryGetValue(nodeId, out var shadowVal))
                        {
                            if (plcRawValue is float fNew && shadowVal is float fOld)
                            {
                                if (Math.Abs(fNew - fOld) < 0.001f) continue;
                            }
                            else if (Equals(shadowVal, plcRawValue))
                            {
                                continue;
                            }
                        }

                        // ====== 确实发生变动 ======
                        _shadowCache[nodeId] = plcRawValue;
                        OnTagStatusChanged?.Invoke(nodeId, "正常", plcRawValue);

                        //  plcRawValue 发送给 OPC UA
                        _opcUaClient.WriteNode(nodeId, plcRawValue);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[内核异动警告] PLC高频读取循环异常: {ex.Message}");
            }
        }
    }
    /// <summary>
    /// 安全停止网关引擎，完全释放物理句柄
    /// </summary>
    public void Stop()
    {
        _cts?.Cancel();
        _opcUaClient.Disconnect();
        _s7Driver.Disconnect();
        OnEngineLog?.Invoke("网关核心引擎已执行安全断开并平稳下线。");
    }
}