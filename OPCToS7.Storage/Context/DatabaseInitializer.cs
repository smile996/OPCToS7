using OPCToS7.Storage.Entities;
using System.Linq;

namespace OPCToS7.Storage.Context;

public static class DatabaseInitializer
{
    /// <summary>
    /// 初始化数据库，并植入默认配置（仅在数据库为空时执行）
    /// </summary>
    public static void Initialize()
    {
        // 实例化上下文，触发自动检测与创建
        using var context = new AppDbContext();

        // 1. 如果PLC配置为空，植入一条默认的西门子1511站点配置
        if (!context.PlcConfigs.Any())
        {
            context.PlcConfigs.Add(new PlcConfig
            {
                ConnectionName = "西门子 S7-1511 站点",
                IpAddress = "192.168.1.10",
                Rack = 0,
                Slot = 1,
                CycleTimeMs = 10 // 极致性能 10ms 转发频次
            });
        }

        // 2. 如果OPC UA配置为空，植入一条默认的本地测试服务器配置
        if (!context.OpcConfigs.Any())
        {
            context.OpcConfigs.Add(new OpcConfig
            {
                ServerUrl = "opc.tcp://127.0.0.1:4840",
                UseSecurity = false
            });
        }

        // 保存默认数据
        context.SaveChanges();
    }
}