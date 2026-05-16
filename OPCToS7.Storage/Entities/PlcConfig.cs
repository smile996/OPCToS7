using System.ComponentModel.DataAnnotations;

namespace OPCToS7.Storage.Entities;

public class PlcConfig
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(50)]
    public string ConnectionName { get; set; } = "S7-1511 Station";

    [Required]
    [MaxLength(15)]
    public string IpAddress { get; set; } = "192.168.1.10";

    public int Rack { get; set; } = 0;
    public int Slot { get; set; } = 1; // S7-1500 默认槽号通常为 1

    public int CycleTimeMs { get; set; } = 10; // 转发周期（性能优先，默认10ms）
}