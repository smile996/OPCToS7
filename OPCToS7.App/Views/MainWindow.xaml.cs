using System.Windows;
using System.Windows.Controls;
using OPCToS7.App.ViewModels;
using OPCToS7.Storage.Entities;
using Microsoft.Win32;
using System.IO;
using System.Text;
using System.Collections.Generic;


namespace OPCToS7.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        this.DataContext = _viewModel;
    }

    private async void StartEngine_Click(object sender, RoutedEventArgs e) => await _viewModel.StartEngineAsync();
    private void StopEngine_Click(object sender, RoutedEventArgs e) => _viewModel.StopEngine();
    private void SaveConfig_Click(object sender, RoutedEventArgs e) => _viewModel.SaveConfiguration();
    private async void BrowseOpc_Click(object sender, RoutedEventArgs e) => await _viewModel.ConnectAndBrowseOpcAsync();

    // 追踪树形列表用户鼠标点击了哪个节点
    private void OpcTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is OpcNodeViewModel selectedNode)
        {
            selectedNode.IsSelected = true;
        }
    }

    // 点击“添加”按钮
    private void AddNode_Click(object sender, RoutedEventArgs e)
    {
        if (OpcTreeView.SelectedItem is OpcNodeViewModel selectedNode)
        {
            _viewModel.AddNodeToMapping(selectedNode);
        }
        else
        {
            MessageBox.Show("请先在左侧的 OPC 树中选择一个变量节点。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    // 点击“删除”按钮
    private void RemoveNode_Click(object sender, RoutedEventArgs e)
    {
        // 检查是否有选中的行
        if (MapDataGrid.SelectedItems.Count == 0)
        {
            MessageBox.Show("请先在右侧表格中点击选中一行或配合 Ctrl/Shift 键多选几行！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 将选中的项倒序复制到一个临时列表中
        var selectedTags = new System.Collections.Generic.List<TagMapEntity>();
        foreach (var item in MapDataGrid.SelectedItems)
        {
            if (item is TagMapEntity tag)
            {
                selectedTags.Add(tag);
            }
        }

        // 执行批量安全剔除
        if (MessageBox.Show($"确定要从网关中物理移除这 {selectedTags.Count} 个变量映射吗？", "操作确认", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
        {
            foreach (var tag in selectedTags)
            {
                _viewModel.TagMappings.Remove(tag);
            }
        }
    }

    private void LogTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox textBox) textBox.ScrollToEnd();
    }

    private void FindAllVariables(OpcNodeViewModel node, List<OpcNodeViewModel> result)
    {
        if (node.NodeClass == "Variable") result.Add(node);
        foreach (var child in node.Children) FindAllVariables(child, result);
    }

    // 导出功能：在模板中新增地址与偏移预留列
    private void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        var allVariables = new List<OpcNodeViewModel>();
        foreach (var rootNode in _viewModel.OpcServerTree)
        {
            FindAllVariables(rootNode, allVariables);
        }

        if (allVariables.Count == 0)
        {
            MessageBox.Show("当前没有扫描到任何 OPC 变量，请先连接并扫描服务器。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SaveFileDialog saveFileDialog = new SaveFileDialog
        {
            Filter = "CSV 文件 (*.csv)|*.csv",
            Title = "导出工业网关智能组态模板",
            FileName = "OPC_Tags_Hybrid_Template.csv"
        };

        if (saveFileDialog.ShowDialog() == true)
        {
            try
            {
                using (StreamWriter sw = new StreamWriter(saveFileDialog.FileName, false, new UTF8Encoding(true)))
                {
                    // 在文件头部增加清晰的现场实施防呆注释
                    sw.WriteLine("# 工业网关混排规则：[DB块/字节偏移/位偏移]可以自己填写绝对数值；若留空或填写了非法字符(如英文)，网关将全自动计算连续偏移。");
                    sw.WriteLine("是否添加(1/0),变量名称,节点ID(NodeId),数据类型,DB块,字节偏移(Byte),位偏移(Bit)");

                    foreach (var node in allVariables)
                    {
                        // 导出时，后三列默认留空，交给现场人员选择性填写
                        sw.WriteLine($"0,{node.DisplayName},{node.NodeId},REAL,,,");
                    }
                }
                MessageBox.Show($"已成功导出 {allVariables.Count} 条高级组态模板！\n如果需要软件自动计算，请保持后三列留空即可。", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"导出失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }


    // 导入功能：解析包含自定义地址的多列 CSV
    private void ImportCsv_Click(object sender, RoutedEventArgs e)
    {
        OpenFileDialog openFileDialog = new OpenFileDialog
        {
            Filter = "CSV 文件 (*.csv)|*.csv",
            Title = "导入离线配置映射表"
        };

        if (openFileDialog.ShowDialog() == true)
        {
            try
            {
                string[] lines = File.ReadAllLines(openFileDialog.FileName, Encoding.UTF8);
                int importCount = 0;

                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (line.StartsWith("#") || line.Contains("是否添加")) continue;

                    string[] columns = line.Split(',');
                    if (columns.Length < 3) continue;

                    // 提取 7 列数据
                    string isAddFlag = columns[0].Trim();
                    string tagName = columns[1].Trim();
                    string nodeId = columns[2].Trim();
                    string dataType = columns.Length >= 4 ? columns[3].Trim() : "REAL";
                    string csvDb = columns.Length >= 5 ? columns[4].Trim() : "";
                    string csvByte = columns.Length >= 6 ? columns[5].Trim() : "";
                    string csvBit = columns.Length >= 7 ? columns[6].Trim() : "";

                    if (isAddFlag == "1" || isAddFlag.ToUpper() == "TRUE")
                    {
                        // 传入重构后的高级混排算法
                        _viewModel.AddNodeToMappingFromCsv(tagName, nodeId, dataType, csvDb, csvByte, csvBit);
                        importCount++;
                    }
                }

                MessageBox.Show($"导入成功！成功加载并校准了 {importCount} 个工业点位物理地址。", "导入成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"导入失败，请检查CSV格式或编码: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}