using System;
using System.Buffers.Binary;
using OPCToS7.Core.Models;

namespace OPCToS7.Core.Siemens;

public class S7BufferPacker
{
    // 每一个西门子 DB 块对应一个固定大小的本地字节缓冲区
    // 假设单个 DB 块最大长度支持 1024 字节（可根据现场实际组态动态调整）
    private readonly byte[] _buffer = new byte[1024];

    public int DbNumber { get; }

    public S7BufferPacker(int dbNumber)
    {
        DbNumber = dbNumber;
    }

    /// <summary>
    /// 获取当前缓冲区的只读快照（发送时直接使用，无需拷贝）
    /// </summary>
    public ReadOnlySpan<byte> GetBufferSpan() => _buffer.AsSpan();

    /// <summary>
    /// 高性能将原始数据解析并打包进本地字节缓冲区
    /// </summary>
    public void Pack(RuntimeTag tag, object rawValue)
    {
        try
        {
            switch (tag.S7Type)
            {
                case S7DataType.REAL:
                    if (rawValue is float fVal)
                    {
                        // 0内存分配地将浮点数以大端格式打入指定字节偏移处
                        BinaryPrimitives.WriteSingleBigEndian(_buffer.AsSpan(tag.ByteOffset, 4), fVal);
                    }
                    else if (rawValue is double dVal) // 兼容可能发生的精度隐式转换
                    {
                        BinaryPrimitives.WriteSingleBigEndian(_buffer.AsSpan(tag.ByteOffset, 4), (float)dVal);
                    }
                    break;

                case S7DataType.INT:
                    short sVal = Convert.ToInt16(rawValue);
                    BinaryPrimitives.WriteInt16BigEndian(_buffer.AsSpan(tag.ByteOffset, 2), sVal);
                    break;

                case S7DataType.WORD:
                    ushort usVal = Convert.ToUInt16(rawValue);
                    BinaryPrimitives.WriteUInt16BigEndian(_buffer.AsSpan(tag.ByteOffset, 2), usVal);
                    break;

                case S7DataType.DINT:
                    int iVal = Convert.ToInt32(rawValue);
                    BinaryPrimitives.WriteInt32BigEndian(_buffer.AsSpan(tag.ByteOffset, 4), iVal);
                    break;

                case S7DataType.BOOL:
                    if (rawValue is bool bVal)
                    {
                        PackBit(tag.ByteOffset, tag.BitOffset, bVal);
                    }
                    break;
            }
        }
        catch (Exception)
        {
            // 通讯层数据转换异常捕获，防止因为外部输入源类型脏数据导致网关崩溃
        }
    }

    /// <summary>
    /// 精准控制字节中某一位（Bit）的读写逻辑
    /// </summary>
    private void PackBit(int byteOffset, int bitOffset, bool value)
    {
        lock (_buffer) // 防止多线程冲突破坏单个字节的位操作
        {
            if (value)
            {
                // 将对应位置 1
                _buffer[byteOffset] |= (byte)(1 << bitOffset);
            }
            else
            {
                // 将对应位置 0
                _buffer[byteOffset] &= (byte)~(1 << bitOffset);
            }
        }
    }
}