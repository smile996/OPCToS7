using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Runtime.CompilerServices;

namespace OPCToS7.Storage.Entities;

public class TagMapEntity : INotifyPropertyChanged
{
    [Key]
    public int Id { get; set; }
    public bool IsActive { get; set; } = true;
    public string TagName { get; set; } = string.Empty;
    public string OpcNodeId { get; set; } = string.Empty;
    public string DataType { get; set; } = "REAL";
    public string DbNumber { get; set; } = "1";
    public int ByteOffset { get; set; }
    public int BitOffset { get; set; }

    // ====== 变量级的通讯状态诊断（不存入SQLite） ======
    private string _linkStatus = "未连接";

    [NotMapped]
    public string LinkStatus
    {
        get => _linkStatus;
        set
        {
            if (_linkStatus != value)
            {
                _linkStatus = value;
                OnPropertyChanged();
            }
        }
    }

    private object? _currentValue;
    [NotMapped]
    public object? CurrentValue
    {
        get => _currentValue;
        set
        {
            if (!Equals(_currentValue, value))
            {
                _currentValue = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}