using System.ComponentModel.DataAnnotations;

namespace OPCToS7.Storage.Entities;

public class OpcConfig
{
    [Key]
    public int Id { get; set; }

    [Required]
    public string ServerUrl { get; set; } = "opc.tcp://127.0.0.1:4840";

    public bool UseSecurity { get; set; } = false;

    public string? UserName { get; set; }
    public string? Password { get; set; }
}