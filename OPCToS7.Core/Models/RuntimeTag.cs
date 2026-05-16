namespace OPCToS7.Core.Models;

/// <summary>
/// 运行时高性能标签模型
/// </summary>
public class RuntimeTag
{
    public required string OpcNodeId { get; init; }
    public required string DataType { get; init; }
    public required int DbNumber { get; init; }
    public required int ByteOffset { get; init; }
    public required int BitOffset { get; init; }

    // 预留解析后的类型枚举，避免在循环中重复进行字符串比对
    public S7DataType S7Type { get; set; }
}

public enum S7DataType
{
    BOOL,
    INT,    // 16位有符号整数 (2字节)
    WORD,   // 16位无符号整数 (2字节)
    DINT,   // 32位有符号整数 (4字节)
    REAL    // 32位浮点数 (4字节)
}