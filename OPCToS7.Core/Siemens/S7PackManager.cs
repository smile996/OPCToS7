using OPCToS7.Core.Models;
using System;
using System.Collections.Concurrent;
using System.Text;

namespace OPCToS7.Core.Engine;

/// <summary>
/// 西门子 S7 协议高性能内存打包与字节序翻转管理器
/// </summary>
public class S7PackManager
{
    // 这里用于存放准备发往 PLC 的字节缓存队列或影子寄存器
    private ConcurrentDictionary<string, byte[]> _writeBuffer = new();

    /// <summary>
    /// 将 OPC 收到的动态对象，严格按照西门子物理类型打包为字节流（并处理大小端翻转）
    /// </summary>
    public void UpdateTagValue(RuntimeTag tag, object rawValue)
    {
        if (rawValue == null) return;

        try
        {
            byte[] payload = null;

            switch (tag.DataType.ToUpper())
            {
                case "REAL":
                    float floatVal = Convert.ToSingle(rawValue);
                    payload = BitConverter.GetBytes(floatVal);
                    Array.Reverse(payload); // PC是小端，西门子是大端，必须翻转字节序
                    break;

                case "DINT":
                    int dintVal = Convert.ToInt32(rawValue);
                    payload = BitConverter.GetBytes(dintVal);
                    Array.Reverse(payload);
                    break;

                case "DWORD":
                    uint dwordVal = Convert.ToUInt32(rawValue);
                    payload = BitConverter.GetBytes(dwordVal);
                    Array.Reverse(payload);
                    break;

                case "INT":
                    short intVal = Convert.ToInt16(rawValue);
                    payload = BitConverter.GetBytes(intVal);
                    Array.Reverse(payload);
                    break;

                case "WORD":
                    ushort wordVal = Convert.ToUInt16(rawValue);
                    payload = BitConverter.GetBytes(wordVal);
                    Array.Reverse(payload);
                    break;

                case "BOOL":
                    bool boolVal = Convert.ToBoolean(rawValue);
                    // 布尔值在 S7.Net 等库中通常按字节写入或位覆写
                    payload = new byte[] { boolVal ? (byte)1 : (byte)0 };
                    break;

                case "STRING":
                   
                    string strValue = Convert.ToString(rawValue) ?? "";

                    // 西门子 S7 字符串固定为 256 字节长度的大数组
                    payload = new byte[256];

                    // 第0字节：西门子字符串最大允许长度 (254)
                    payload[0] = 254;

                    // 将字符串转为 ASCII 字节序列
                    byte[] strBytes = Encoding.ASCII.GetBytes(strValue);

                    // 防止 OPC 发来的字符串太长导致内存越界
                    int actualLength = Math.Min(strBytes.Length, 254);

                    // 第1字节：当前字符串实际有效长度
                    payload[1] = (byte)actualLength;

                    // 把真实字符内容拷贝进 payload (从第2个字节开始)
                    Array.Copy(strBytes, 0, payload, 2, actualLength);

                    // 字符串绝对不要执行 Array.Reverse(payload) 翻转
                    break;
            }

            if (payload != null)
            {
                // 将打包好的大端字节流放入写缓存区，等待底层通讯线程将其刷入 PLC 的 DB 块
                // Key 可以是 "DB1.DBD4" 这种绝对地址
                string cacheKey = $"DB{tag.DbNumber}_Byte{tag.ByteOffset}_Bit{tag.BitOffset}";
                _writeBuffer[cacheKey] = payload;
            }
        }
        catch (Exception ex)
        {
            // 如果 OPC 发来的数据无法转换为目标类型（比如把 "ABC" 强转为 REAL）
            // 这里可以触发日志，或者静默抛弃，防止引擎崩溃
            Console.WriteLine($"[打包器错误] 节点 {tag.OpcNodeId} 封包失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 从 PLC 读取到的大块原始字节数组中，精准切割并还原出 C# 数据类型（处理大小端）
    /// </summary>
    public object? ExtractTagValue(RuntimeTag tag, byte[] dbBuffer)
    {
        try
        {
            // 防越界保护：如果配置的偏移量超出了实际读到的 buffer 长度，直接忽略
            if (tag.ByteOffset >= dbBuffer.Length) return null;

            switch (tag.DataType.ToUpper())
            {
                case "REAL":
                    if (tag.ByteOffset + 4 > dbBuffer.Length) return null;
                    byte[] realBytes = new byte[4];
                    Array.Copy(dbBuffer, tag.ByteOffset, realBytes, 0, 4);
                    Array.Reverse(realBytes); // 西门子大端转PC小端
                    return BitConverter.ToSingle(realBytes, 0);

                case "DINT":
                case "DWORD":
                    if (tag.ByteOffset + 4 > dbBuffer.Length) return null;
                    byte[] dintBytes = new byte[4];
                    Array.Copy(dbBuffer, tag.ByteOffset, dintBytes, 0, 4);
                    Array.Reverse(dintBytes);
                    return tag.DataType.ToUpper() == "DINT" ? BitConverter.ToInt32(dintBytes, 0) : BitConverter.ToUInt32(dintBytes, 0);

                case "INT":
                case "WORD":
                    if (tag.ByteOffset + 2 > dbBuffer.Length) return null;
                    byte[] intBytes = new byte[2];
                    Array.Copy(dbBuffer, tag.ByteOffset, intBytes, 0, 2);
                    Array.Reverse(intBytes);
                    return tag.DataType.ToUpper() == "INT" ? BitConverter.ToInt16(intBytes, 0) : BitConverter.ToUInt16(intBytes, 0);

                case "BOOL":
                    // 位运算：提取指定字节中的指定位
                    byte b = dbBuffer[tag.ByteOffset];
                    return (b & (1 << tag.BitOffset)) != 0;

                case "STRING":
                    // 解析西门子字符串头部
                    if (tag.ByteOffset + 2 > dbBuffer.Length) return "";
                    int actualLength = dbBuffer[tag.ByteOffset + 1]; // 第1字节是实际长度

                    if (actualLength <= 0 || tag.ByteOffset + 2 + actualLength > dbBuffer.Length) return "";

                    // 提取真实字符（ASCII编码）
                    return System.Text.Encoding.ASCII.GetString(dbBuffer, tag.ByteOffset + 2, actualLength);

                default:
                    return null;
            }
        }
        catch (Exception)
        {
            return null; // 解包失败静默返回，防止单个脏数据搞崩整个引擎
        }
    }

    /// <summary>
    /// 获取当前所有处于活跃状态并等待写入 PLC 的数据载荷，同时清空队列避免重复发送
    /// </summary>
    /// <returns>包含目标绝对地址(Key)和对应大端字节流(Value)的字典快照</returns>
    public System.Collections.Generic.Dictionary<string, byte[]> GetActivePayloads()
    {
        // 1. 拍摄当前并发字典的快照，防止在遍历写入 PLC 时被其他线程修改
        var payloads = new System.Collections.Generic.Dictionary<string, byte[]>(_writeBuffer);

        // 2. 清空缓存区！
        _writeBuffer.Clear();

        return payloads;
    }
}