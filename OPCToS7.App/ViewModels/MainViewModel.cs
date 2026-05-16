using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Threading;
using OPCToS7.Core.Engine;
using OPCToS7.Core.Models;
using OPCToS7.Storage.Context;
using OPCToS7.Storage.Entities;

namespace OPCToS7.App.ViewModels;

public class MainViewModel : ViewModelBase
{
    private readonly TransferEngine _engine = new();
    private readonly DispatcherTimer _uiRefreshTimer = new();
    private readonly ConcurrentQueue<string> _logQueue = new();

    private bool _isOpcConnected;
    private bool _isS7Connected;
    private bool _isEngineRunning;
    private string _logText = string.Empty;
    private PlcConfig _plcConfig = null!;
    private OpcConfig _opcConfig = null!;

    public bool IsOpcConnected
    {
        get { return _isOpcConnected; }
        set { SetProperty(ref _isOpcConnected, value); }
    }

    public bool IsS7Connected
    {
        get { return _isS7Connected; }
        set { SetProperty(ref _isS7Connected, value); }
    }

    public bool IsEngineRunning
    {
        get { return _isEngineRunning; }
        set { SetProperty(ref _isEngineRunning, value); }
    }

    public string LogText
    {
        get { return _logText; }
        set { SetProperty(ref _logText, value); }
    }

    public PlcConfig PlcConfig
    {
        get { return _plcConfig; }
        set { SetProperty(ref _plcConfig, value); }
    }

    public OpcConfig OpcConfig
    {
        get { return _opcConfig; }
        set { SetProperty(ref _opcConfig, value); }
    }

    public ObservableCollection<TagMapEntity> TagMappings { get; } = new();

    public MainViewModel()
    {
        // 载入本地持久化配置
        LoadConfiguration();

        // 关联核心通讯引擎的状态回调
        _engine.OnOpcStatusChanged += status => IsOpcConnected = status;
        _engine.OnS7StatusChanged += status => IsS7Connected = status;
        _engine.OnEngineLog += msg => _logQueue.Enqueue($"[{DateTime.Now:HH:mm:ss.fff}] {msg}\r\n");

        // 绑定实时数据回传事件
        _engine.OnTagStatusChanged += ApplyTagStatusUpdate;

        //高性能降频定时器：每200ms将日志队列刷入UI，防止界面由于10ms级的通讯卡死
        _uiRefreshTimer.Interval = TimeSpan.FromMilliseconds(200);
        _uiRefreshTimer.Tick += FlushLogsToUi;
        _uiRefreshTimer.Start();
    }

    /// <summary>
    /// 从 SQLite 中提取用户上次保存的工业现场参数与映射表
    /// </summary>
    private void LoadConfiguration()
    {
        using var db = new AppDbContext();
        PlcConfig = db.PlcConfigs.First();
        OpcConfig = db.OpcConfigs.First();

        TagMappings.Clear();
        foreach (var entity in db.TagMapEntities.ToList())
        {
            TagMappings.Add(entity);
        }
    }

    /// <summary>
    /// 保存当前工作台的组态配置到本地数据库
    /// </summary>
    public void SaveConfiguration()
    {
        using var db = new AppDbContext();

        // 更新连接配置
        db.PlcConfigs.Update(PlcConfig);
        db.OpcConfigs.Update(OpcConfig);

        // 清理并重新注入最新的映射表
        var oldTags = db.TagMapEntities.ToList();
        db.TagMapEntities.RemoveRange(oldTags);
        db.TagMapEntities.AddRange(TagMappings);

        db.SaveChanges();
        _logQueue.Enqueue($"[{DateTime.Now:HH:mm:ss.fff}] [系统] 工业工作台组态参数已成功固化到本地SQLite数据库。\r\n");
    }

    public ObservableCollection<OpcNodeViewModel> OpcServerTree { get; } = new();

    public async Task ConnectAndBrowseOpcAsync()
    {
        if (IsOpcConnected) return;

        _logQueue.Enqueue($"[{DateTime.Now:HH:mm:ss.fff}] [系统] 正在尝试连接 OPC UA 服务器以扫描节点树...\r\n");

        bool connected = await _engine.GetOpcClientManager().ConnectAsync(OpcConfig.ServerUrl);

        if (connected)
        {
            OpcServerTree.Clear();

            var rootNode = new OpcNodeViewModel(_engine.GetOpcClientManager(), "i=85", "Objects (根目录)", "Object", true);

            OpcServerTree.Add(rootNode);

            rootNode.IsExpanded = true;
        }
    }

    private void ApplyTagStatusUpdate(string nodeId, string status, object? value)
    {
        System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {

            var targetRow = TagMappings.FirstOrDefault(t => t.OpcNodeId == nodeId);
            if (targetRow != null)
            {
                targetRow.LinkStatus = status;  
                targetRow.CurrentValue = value; 
            }
        });
    }

    /// <summary>
    /// 启动中间件转发引擎
    /// </summary>
    public async Task StartEngineAsync()
    {
        if (IsEngineRunning) return;

        SaveConfiguration();

        var activeRuntimeTags = TagMappings
            .Where(t => t.IsActive)
            .Select(t => new RuntimeTag
            {
                OpcNodeId = t.OpcNodeId,
                DataType = t.DataType,
                DbNumber = int.TryParse(t.DbNumber, out int dbNum) ? dbNum : 1,
                ByteOffset = t.ByteOffset,
                BitOffset = t.BitOffset
            })
            .ToList();

        if (activeRuntimeTags.Count == 0)
        {
            _logQueue.Enqueue($"[{DateTime.Now:HH:mm:ss.fff}] [警告] 当前未勾选任何处于激活状态的标签映射，拒绝启动转发引擎。\r\n");
            return;
        }

        IsEngineRunning = true;

        // 唤醒全双工总调度引擎
        await _engine.StartAsync(
            OpcConfig.ServerUrl,
            PlcConfig.IpAddress,
            PlcConfig.Rack,
            PlcConfig.Slot,
            activeRuntimeTags,
            PlcConfig.CycleTimeMs
        );
    }

    /// <summary>
    /// 停止转发引擎
    /// </summary>
    public void StopEngine()
    {
        if (!IsEngineRunning) return;
        _engine.Stop();
        IsEngineRunning = false;
        IsOpcConnected = false;
        IsS7Connected = false;
    }

    private void FlushLogsToUi(object? sender, EventArgs e)
    {
        if (_logQueue.IsEmpty) return;

        var sb = new StringBuilder(LogText);
        while (_logQueue.TryDequeue(out string? log))
        {
            sb.Append(log);
        }

        if (sb.Length > 100000)
        {
            sb.Remove(0, 50000);
        }

        LogText = sb.ToString();
    }

    /// <summary>
    /// 智能添加节点到映射表（支持变量名与STRING连续排块）
    /// </summary>
    public void AddNodeToMapping(OpcNodeViewModel? selectedNode)
    {
        if (selectedNode == null || selectedNode.NodeClass != "Variable") return;
        if (TagMappings.Any(t => t.OpcNodeId == selectedNode.NodeId)) return;

        int nextByte = 0;
        int nextBit = 0;
        string currentDb = "1";
        string defaultType = "REAL";

        if (TagMappings.Count > 0)
        {
            var lastTag = TagMappings.Last();
            currentDb = lastTag.DbNumber;
            nextByte = lastTag.ByteOffset;
            nextBit = lastTag.BitOffset;

            if (lastTag.DataType.ToUpper() == "BOOL")
            {
                nextBit++;
                if (nextBit > 7) { nextBit = 0; nextByte++; }
            }
            else
            {
                nextByte += GetS7TypeByteSize(lastTag.DataType);
                nextBit = 0;
            }
        }

        TagMappings.Add(new TagMapEntity
        {
            IsActive = true,
            TagName = selectedNode.DisplayName, 
            OpcNodeId = selectedNode.NodeId,
            DataType = defaultType,
            DbNumber = currentDb,
            ByteOffset = nextByte,
            BitOffset = nextBit
        });
    }


    public void AddNodeToMappingFromCsv(string tagName, string nodeId, string rawDataType, string csvDb, string csvByte, string csvBit)
    {
        // 防止重复导入相同节点
        if (TagMappings.Any(t => t.OpcNodeId == nodeId)) return;

        // 数据类型清洗与防呆
        string cleanDataType = rawDataType.Trim().ToUpper();
        var allowedTypes = new List<string> { "REAL", "DINT", "INT", "WORD", "BOOL", "STRING" };
        if (!allowedTypes.Contains(cleanDataType))
        {
            cleanDataType = "REAL"; // 遇到非法类型字符，默认降级为 REAL
        }

        // 尝试解析 CSV 中手动填写的物理地址数据
        bool hasUserDb = int.TryParse(csvDb, out int userDb);
        bool hasUserByte = int.TryParse(csvByte, out int userByte);
        bool hasUserBit = int.TryParse(csvBit, out int userBit);

        string targetDb;
        int targetByte;
        int targetBit;

        // 如果用户填写了合法的 DB块 和 字节偏移，则直接采用用户的自定义地址
        if (hasUserDb && hasUserByte)
        {
            targetDb = userDb.ToString();
            targetByte = userByte;
            // 如果填了位偏移则用填的，没填则默认为 0
            targetBit = hasUserBit ? userBit : 0;
        }
        else
        {
            //如果留空或者输入了非法字符，由软件根据上一行推算连续地址
            if (TagMappings.Count > 0)
            {
                var lastTag = TagMappings.Last();
                targetDb = lastTag.DbNumber; 
                targetByte = lastTag.ByteOffset;
                targetBit = lastTag.BitOffset;

                if (lastTag.DataType.ToUpper() == "BOOL")
                {
                    targetBit++;
                    if (targetBit > 7) { targetBit = 0; targetByte++; }
                }
                else
                {
                    targetByte += GetS7TypeByteSize(lastTag.DataType);
                    targetBit = 0;
                }
            }
            else
            {
                // 如果是第一行且用户没填，则初始化默认根地址
                targetDb = "1";
                targetByte = 0;
                targetBit = 0;
            }
        }

        // 将最终确定的地址结构压入 WPF 绑定的 DataGrid 集合
        TagMappings.Add(new TagMapEntity
        {
            IsActive = true,
            TagName = string.IsNullOrWhiteSpace(tagName) ? "未命名变量" : tagName,
            OpcNodeId = nodeId,
            DataType = cleanDataType,
            DbNumber = targetDb,
            ByteOffset = targetByte,
            BitOffset = targetBit
        });
    }
    /// <summary>
    /// 测算西门子标准数据类型的字节物理长度
    /// </summary>
    private int GetS7TypeByteSize(string dataType)
    {
        return dataType.ToUpper() switch
        {
            "STRING" => 256, // 西门子默认 STRING 占用 256 字节 (2字节头 + 254字节内容)
            "REAL" => 4,
            "DINT" => 4,
            "DWORD" => 4,
            "INT" => 2,
            "WORD" => 2,
            "BYTE" => 1,
            _ => 2
        };
    }
}