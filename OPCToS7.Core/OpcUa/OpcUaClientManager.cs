using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Opc.Ua;
using Opc.Ua.Client;

namespace OPCToS7.Core.OpcUa;

public class OpcUaClientManager
{
    private Session? _session;
    private Subscription? _subscription;
    private bool _isConnected;

    public event Action<bool>? OnConnectionStatusChanged;
    public event Action<string>? OnLogMessage;
    public event Action<string, object>? OnTagValueChanged;

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
    /// 异步建立与 OPC UA 服务器的物理连接
    /// </summary>
    public async Task<bool> ConnectAsync(string serverUrl)
    {
        try
        {
            OnLogMessage?.Invoke($"正在连接到 OPC UA 服务器: {serverUrl}...");

            // 1. 构造基础配置 
            var config = new ApplicationConfiguration
            {
                ApplicationName = "OPCToS7 Gateway",
                ApplicationUri = $"urn:{System.Net.Dns.GetHostName()}:OPCToS7Gateway",
                ApplicationType = ApplicationType.Client,
                SecurityConfiguration = new SecurityConfiguration
                {
                    ApplicationCertificate = new CertificateIdentifier
                    {
                        StoreType = @"Directory",
                        StorePath = @"%CommonApplicationData%\OPC Foundation\CertificateStores\MachineDefault",
                        SubjectName = "CN=OPCToS7 Gateway, C=CN"
                    },
                    TrustedPeerCertificates = new CertificateTrustList
                    {
                        StoreType = @"Directory",
                        StorePath = @"%CommonApplicationData%\OPC Foundation\CertificateStores\UA Certificate Authorities"
                    },
                    TrustedIssuerCertificates = new CertificateTrustList
                    {
                        StoreType = @"Directory",
                        StorePath = @"%CommonApplicationData%\OPC Foundation\CertificateStores\UA Certificate Authorities"
                    },
                    RejectedCertificateStore = new CertificateTrustList
                    {
                        StoreType = @"Directory",
                        StorePath = @"%CommonApplicationData%\OPC Foundation\CertificateStores\RejectedCertificates"
                    },
                    AutoAcceptUntrustedCertificates = true,
                    RejectSHA1SignedCertificates = false
                },
                TransportConfigurations = new TransportConfigurationCollection(),
                TransportQuotas = new TransportQuotas { OperationTimeout = 15000 },
                ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60000 }
            };

            await config.Validate(ApplicationType.Client);

            if (config.SecurityConfiguration.AutoAcceptUntrustedCertificates)
            {
                config.CertificateValidator.CertificateValidation += (s, e) => { e.Accept = true; };
            }

            // 2. 获取端点并选择安全策略
            Uri uri = new Uri(serverUrl);
            var endpointDescription = CoreClientUtils.SelectEndpoint(config, uri.ToString(), useSecurity: false);

            endpointDescription.EndpointUrl = endpointDescription.EndpointUrl.Replace(new Uri(endpointDescription.EndpointUrl).Host, uri.Host);

            var endpointConfiguration = EndpointConfiguration.Create(config);
            var endpoint = new ConfiguredEndpoint(null, endpointDescription, endpointConfiguration);

            // 3. 异步建立 Session 会话
            _session = await Session.Create(config, endpoint, false, "OPCToS7_Session", 60000, null, null);

            _session.KeepAlive += (sender, e) =>
            {
                if (ServiceResult.IsBad(e.Status))
                {
                    OnLogMessage?.Invoke($"OPC UA 服务器心跳丢失: {e.Status}，准备触发自愈重连...");
                    IsConnected = false;
                }
            };

            IsConnected = true;
            OnLogMessage?.Invoke("OPC UA 服务器连接成功！会话激活。");
            return true;
        }
        catch (Exception ex)
        {
            IsConnected = false;
            OnLogMessage?.Invoke($"OPC UA 连接失败: {ex.Message}");
            return false;
        }
    }

    public List<ReferenceDescription> BrowseChildren(string parentNodeId)
    {
        var nodes = new List<ReferenceDescription>();
        if (_session == null || !IsConnected) return nodes;

        try
        {
            var browseDesc = new BrowseDescription
            {
                NodeId = new NodeId(parentNodeId),
                BrowseDirection = BrowseDirection.Forward,
                ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences,
                IncludeSubtypes = true,
                NodeClassMask = (uint)(NodeClass.Object | NodeClass.Variable),
                ResultMask = (uint)BrowseResultMask.All
            };

            _session.Browse(null, null, 0, new BrowseDescriptionCollection { browseDesc }, out var results, out var diagnosticInfos);

            if (results != null && results.Count > 0 && results[0].References != null)
            {
                nodes.AddRange(results[0].References);
            }
        }
        catch (Exception ex)
        {
            OnLogMessage?.Invoke($"扫描节点 [{parentNodeId}] 失败: {ex.Message}");
        }

        return nodes;
    }

    /// <summary>
    /// 动态批量订阅勾选好的工业标签数据（核心数据源）
    /// </summary>
    public void SubscribeTags(IEnumerable<string> nodeIds, int publishingIntervalMs = 50)
    {
        if (_session == null || !IsConnected) return;

        try
        {
            if (_subscription != null)
            {
                _session.RemoveSubscription(_subscription);
            }

            _subscription = new Subscription(_session.DefaultSubscription)
            {
                PublishingInterval = publishingIntervalMs,
                TimestampsToReturn = TimestampsToReturn.Both
            };

            foreach (var id in nodeIds)
            {
                var item = new MonitoredItem(_subscription.DefaultItem)
                {
                    DisplayName = id,
                    StartNodeId = new NodeId(id),
                    AttributeId = Attributes.Value
                };

                item.Notification += OnMonitoredItemNotification;
                _subscription.AddItem(item);
            }

            _session.AddSubscription(_subscription);
            _subscription.Create();
            OnLogMessage?.Invoke($"成功订阅 {_subscription.MonitoredItemCount} 个 OPC UA 变量。进入事件驱动模式。");
        }
        catch (Exception ex)
        {
            OnLogMessage?.Invoke($"批量订阅执行失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 事件驱动回调：只有当现场PLC/数据源变量的值变了，才会触发此处代码
    /// </summary>
    private void OnMonitoredItemNotification(MonitoredItem monitoredItem, MonitoredItemNotificationEventArgs e)
    {
        if (e.NotificationValue is MonitoredItemNotification notification)
        {
            object rawValue = notification.Value.Value;
            if (rawValue != null)
            {
                OnTagValueChanged?.Invoke(monitoredItem.StartNodeId.ToString(), rawValue);
            }
        }
    }
    /// <summary>
    /// 物理写入单个节点数据到 OPC UA 服务器 (基于官方标准服务规范)
    /// </summary>
    /// <param name="nodeId">符合命名空间规范的节点标示字符串 (如: ns=3;i=1008)</param>
    /// <param name="value">经由 PLC 解包出来的 C# 强类型数据对象</param>
    /// <returns>写入成功返回 true，被服务器拒绝或网络异常返回 false</returns>
    public bool WriteNode(string nodeId, object value)
    {
        // 核心防御性编程：确保通讯会话处于可用激活状态
        if (_session == null || !IsConnected) return false;

        try
        {
            // 1. 构造官方规范标准的写入数据元集合 (支持批量写入，此处包装单个节点)
            WriteValueCollection valuesToWrite = new WriteValueCollection();

            WriteValue writeValue = new WriteValue
            {
                NodeId = new NodeId(nodeId),
                AttributeId = Attributes.Value, // 明确告诉服务器你要改的是 Value 属性，而不是浏览名
                Value = new DataValue(new Variant(value))
            };
            valuesToWrite.Add(writeValue);

            // 2. 调用当前 Session 会话的长连接通道执行物理写入
            _session.Write(
                null,                           // 默认请求头
                valuesToWrite,                  // 写入集合
                out StatusCodeCollection results, // 服务器返回的状态码集合
                out DiagnosticInfoCollection diagnosticInfos); // 诊断信息

            // 3. 校验最终结果状态
            if (results != null && results.Count > 0)
            {
                // 检查服务器返回的第一个节点的 StatusCode 是否为 Good (0x00000000)
                return StatusCode.IsGood(results[0]);
            }

            return false;
        }
        catch (Exception ex)
        {
            // 抛出日志记录，不让写入断线等异常破坏外层的长循环线程
            OnLogMessage?.Invoke($"反向同步写入 OPC 节点 [{nodeId}] 出现异常: {ex.Message}");
            return false;
        }
    }

    public void Disconnect()
    {
        try
        {
            _subscription?.Delete(true);
            _session?.Close();
        }
        catch { }
        finally
        {
            IsConnected = false;
        }
    }
}