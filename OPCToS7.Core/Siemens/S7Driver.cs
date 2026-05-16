using System;
using System.Threading;
using System.Threading.Tasks;
using Sharp7;

namespace OPCToS7.Core.Siemens;

public class S7Driver
{
    private readonly S7Client _client = new();
    private readonly object _lockObj = new();

    private string _ip = "192.168.1.10";
    private int _rack = 0;
    private int _slot = 1;

    private bool _isConnected;
    private int _reconnectDelayMs = 1000; // 初始重连间隔 1秒

    // 向上层抛出的连接状态变更事件
    public event Action<bool>? OnConnectionStatusChanged;
    // 向上层抛出通信错误日志事件
    public event Action<string>? OnLogMessage;

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (_isConnected != value)
            {
                _isConnected = value;
                OnConnectionStatusChanged?.Invoke(_isConnected);
            }
        }
    }

    /// <summary>
    /// 初始化并异步启动S7连接守护监视器
    /// </summary>
    public void Initialize(string ip, int rack, int slot)
    {
        _ip = ip;
        _rack = rack;
        _slot = slot;
    }

    /// <summary>
    /// 纯异步连接守护线程
    /// </summary>
    public async Task StartConnectLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (!IsConnected)
            {
                OnLogMessage?.Invoke($"正在尝试连接到西门子S7-1511 PLC [{_ip}]...");

                // 将同步阻塞连接扔到线程池，确保不卡主进程
                int result = await Task.Run(() => _client.ConnectTo(_ip, _rack, _slot), token);

                if (result == 0)
                {
                    IsConnected = true;
                    _reconnectDelayMs = 1000; // 连接成功，重置重连步进时间
                    OnLogMessage?.Invoke("西门子S7-1511连接成功！通道建立。");
                }
                else
                {
                    IsConnected = false;
                    OnLogMessage?.Invoke($"连接失败，错误码: 0x{result:X4}。正在准备重连...");

                    await Task.Delay(_reconnectDelayMs, token);
                    _reconnectDelayMs = Math.Min(_reconnectDelayMs * 2, 30000); // 指数退避
                }
            }
            else
            {
                // 通用物理心跳方案
                bool isAlive = false;
                lock (_lockObj)
                {
                    isAlive = _client.Connected;
                }

                if (!isAlive)
                {
                    OnLogMessage?.Invoke("检测到与PLC的 TCP 物理连接已断开，强制切入重连状态机...");
                    Disconnect();
                }

                await Task.Delay(2000, token); // 每2秒校验一次物理链路
            }
        }
    }

    /// <summary>
    /// 高性能大块数据精准写入方法（支持指定起始字节偏移量）
    /// </summary>
    public bool WriteDbBlock(int dbNumber, int startByteOffset, byte[] payload)
    {
        if (!IsConnected) return false;

        lock (_lockObj)
        {
            int result = _client.DBWrite(dbNumber, startByteOffset, payload.Length, payload);

            if (result == 0)
            {
                return true;
            }
            else
            {
                OnLogMessage?.Invoke($"数据精准写入 DB{dbNumber}.DBD{startByteOffset} 失败，错误码: 0x{result:X4}");

                // 如果是物理网络断开引发的错误，强制执行 Disconnect 触发守护线程去重连
                if (result == 589824 || result == 589826 || !_client.Connected)
                {
                    Disconnect();
                }
                return false;
            }
        }
    }

    /// <summary>
    /// 高性能大块数据读取方法（一次性吸取整块DB内存）
    /// </summary>
    /// <param name="dbNumber">DB块号</param>
    /// <param name="startByte">起始字节</param>
    /// <param name="length">需要读取的总字节长度</param>
    /// <param name="buffer">用于接收数据的缓冲区</param>
    public bool ReadDbBlock(int dbNumber, int startByte, int length, byte[] buffer)
    {
        if (!IsConnected) return false;

        lock (_lockObj)
        {
            // 调用 Sharp7 底层的 DBRead
            int result = _client.DBRead(dbNumber, startByte, length, buffer);

            if (result == 0) return true;

            OnLogMessage?.Invoke($"读取 DB{dbNumber} 失败，错误码: 0x{result:X4}");
            if (result == 589824 || result == 589826 || !_client.Connected)
            {
                Disconnect();
            }
            return false;
        }
    }

    /// <summary>
    /// 显式主动断开
    /// </summary>
    public void Disconnect()
    {
        lock (_lockObj)
        {
            _client.Disconnect();
            IsConnected = false;
        }
    }
}