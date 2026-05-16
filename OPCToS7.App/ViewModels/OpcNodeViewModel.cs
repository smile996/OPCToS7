using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using OPCToS7.Core.OpcUa;

namespace OPCToS7.App.ViewModels;

public class OpcNodeViewModel : ViewModelBase
{
    private readonly OpcUaClientManager _opcManager;
    private bool _isExpanded;
    private bool _isLoaded;
    private bool _isSelected; // 用于追踪用户选中了哪个节点

    public string NodeId { get; }
    public string DisplayName { get; }
    public string NodeClass { get; }

    public ObservableCollection<OpcNodeViewModel> Children { get; } = new();

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            SetProperty(ref _isExpanded, value);

            if (  value && !_isLoaded)
            {
                //将阻塞查询放入后台线程，彻底解放 UI 假死
                _ = LoadChildrenAsync();
            }
        }
    }

    public OpcNodeViewModel(OpcUaClientManager opcManager, string nodeId, string displayName, string nodeClass, bool hasChildren)
    {
        _opcManager = opcManager;
        NodeId = nodeId;
        DisplayName = displayName;
        NodeClass = nodeClass;

        if (hasChildren)
        {
            Children.Add(new OpcNodeViewModel(null!, "Loading...", "正在读取...", "Dummy", false));
        }
    }

    private async Task LoadChildrenAsync()
    {
        try
        {
            // 在后台线程拉取深层节点数据
            var references = await Task.Run(() => _opcManager.BrowseChildren(NodeId));

            // 切回主 UI 线程更新界面树
            Application.Current.Dispatcher.Invoke(() =>
            {
                Children.Clear();
                foreach (var refDesc in references)
                {
                    // 放宽条件：包括变量、对象、属性、方法等
                    bool hasChild = refDesc.NodeClass == Opc.Ua.NodeClass.Object || refDesc.NodeClass == Opc.Ua.NodeClass.Variable;
                    Children.Add(new OpcNodeViewModel(
                        _opcManager,
                        refDesc.NodeId.ToString(),
                        refDesc.DisplayName.Text,
                        refDesc.NodeClass.ToString(),
                        hasChild));
                }
                _isLoaded = true;
            });
        }
        catch (Exception ex)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                Children.Clear();
                Children.Add(new OpcNodeViewModel(null!, "Error", $"读取失败: {ex.Message}", "Dummy", false));
            });
        }
    }
}