using System.Text.Json;
using Opc.Ua;
using Opc.Ua.Server;

namespace TorusGateway.OpcUa
{
    // TORUS Machine Data Model을 OPC UA 주소 공간으로 노출한다 (논문 doi:10.57062/ijpem-st.2022.0024 그림 2).
    // 주소 공간은 Objects/machine 폴더만 두고 시작한다. 클라이언트가 "/machine/...?필터" 형태의 NodeId를 요청하면
    // TORUS getData를 호출해 성공할 때만 그 자리에서 노드(경로 폴더 + 필터 이름의 변수)를 만든다.
    // 실패하면 null을 돌려주고, 스택이 BadNodeIdUnknown으로 응답한다.
    internal sealed class TorusNodeManager : CustomNodeManager2
    {
        internal const string NamespaceUri = "urn:TorusGateway:TORUS";

        private readonly int _pollIntervalMs;
        private readonly Action<string>? _log;

        // 구독(MonitoredItem) 중인 노드. 폴링 스레드가 이 노드들만 주기적으로 TORUS에서 다시 읽는다.
        private readonly Dictionary<uint, BaseDataVariableState> _monitored = new();
        private readonly object _monitoredLock = new();
        private readonly Dictionary<NodeState, string> _lastSignature = new(); // 값 변경 감지 (Lock 안에서만 접근)
        private Thread? _pollThread;
        private volatile bool _polling;

        public TorusNodeManager(IServerInternal server, ApplicationConfiguration configuration, int pollIntervalMs, Action<string>? log)
            : base(server, configuration, NamespaceUri)
        {
            _pollIntervalMs = pollIntervalMs;
            _log = log;
        }

        public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
        {
            lock (Lock)
            {
                if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out var references))
                    externalReferences[ObjectIds.ObjectsFolder] = references = new List<IReference>();

                var root = CreateFolder(null, OpcUaValues.RootPath, "machine");
                root.AddReference(ReferenceTypes.Organizes, true, ObjectIds.ObjectsFolder);
                references.Add(new NodeStateReference(ReferenceTypes.Organizes, false, root.NodeId));
                AddPredefinedNode(SystemContext, root);
            }

            _polling = true;
            _pollThread = new Thread(PollLoop) { IsBackground = true, Name = "OpcUaTorusPoll" };
            _pollThread.Start();
        }

        protected override void Dispose(bool disposing)
        {
            _polling = false;
            _pollThread?.Join(2000);
            base.Dispose(disposing);
        }

        private FolderState CreateFolder(NodeState? parent, string path, string name)
        {
            var folder = new FolderState(parent)
            {
                ReferenceTypeId = ReferenceTypes.Organizes,
                TypeDefinitionId = ObjectTypeIds.FolderType,
                NodeId = new NodeId(path, NamespaceIndex),
                BrowseName = new QualifiedName(name, NamespaceIndex),
                DisplayName = new LocalizedText("en", name),
                WriteMask = AttributeWriteMask.None,
                UserWriteMask = AttributeWriteMask.None,
                EventNotifier = EventNotifiers.None,
            };
            parent?.AddChild(folder);
            return folder;
        }

        // 경로의 각 단계 폴더를 없으면 만들고, 마지막 폴더를 반환한다.
        private NodeState EnsureFolders(string path)
        {
            NodeState parent = PredefinedNodes[new NodeId(OpcUaValues.RootPath, NamespaceIndex)];
            foreach (string folderPath in OpcUaValues.FolderPathsBelowRoot(path))
            {
                if (!PredefinedNodes.TryGetValue(new NodeId(folderPath, NamespaceIndex), out var folder))
                {
                    folder = CreateFolder(parent, folderPath, folderPath.Substring(folderPath.LastIndexOf('/') + 1));
                    AddPredefinedNode(SystemContext, folder);
                }
                parent = folder;
            }
            return parent;
        }

        private static NodeId DataTypeOf(TorusValueKind kind) => kind switch
        {
            TorusValueKind.String => DataTypeIds.String,
            TorusValueKind.Long => DataTypeIds.Int64,
            TorusValueKind.Double => DataTypeIds.Double,
            _ => DataTypeIds.Boolean,
        };

        private BaseDataVariableState CreateVariable(NodeState parent, string identifier, string filter, TorusValue value)
        {
            var variable = new BaseDataVariableState(parent)
            {
                SymbolicName = filter,
                ReferenceTypeId = ReferenceTypes.Organizes,
                TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
                NodeId = new NodeId(identifier, NamespaceIndex),
                BrowseName = new QualifiedName(filter, NamespaceIndex),
                DisplayName = new LocalizedText("en", filter),
                WriteMask = AttributeWriteMask.None,
                UserWriteMask = AttributeWriteMask.None,
                DataType = DataTypeOf(value.Kind),
                ValueRank = value.IsArray ? ValueRanks.OneDimension : ValueRanks.Scalar,
                AccessLevel = AccessLevels.CurrentReadOrWrite,
                UserAccessLevel = AccessLevels.CurrentReadOrWrite,
                Historizing = false,
                Value = value.Value,
                StatusCode = StatusCodes.Good,
                Timestamp = DateTime.UtcNow,
            };
            if (value.IsArray) variable.ArrayDimensions = new ReadOnlyList<uint>(new List<uint> { 0 });
            variable.OnReadValue = OnReadTorusValue;
            variable.OnWriteValue = OnWriteTorusValue;
            variable.OnReadUserAccessLevel = OnReadUserAccessLevel;
            parent.AddChild(variable);
            return variable;
        }

        // 클라이언트가 노드를 처음 참조할 때(읽기, 구독, 브라우즈) 호출된다. 없는 노드면 TORUS에 물어 만든다.
        protected override NodeHandle GetManagerHandle(ServerSystemContext context, NodeId nodeId, IDictionary<NodeId, NodeState> cache)
        {
            lock (Lock)
            {
                if (!IsNodeIdInNamespace(nodeId)) return null!;
                if (PredefinedNodes.TryGetValue(nodeId, out var existing))
                    return new NodeHandle { NodeId = nodeId, Node = existing, Validated = true };

                string? identifier = nodeId.IdType == IdType.String ? nodeId.Identifier as string : null;
                if (!OpcUaValues.TryParseNodePath(identifier, out string path, out string filter)
                    || !TryReadTorus(path, filter, out var value))
                {
                    _log?.Invoke($"UNKNOWN {identifier ?? nodeId.ToString()}");
                    return null!;
                }

                var variable = CreateVariable(EnsureFolders(path), identifier!, filter, value!);
                AddPredefinedNode(SystemContext, variable);
                _log?.Invoke($"NODE    {identifier}");
                return new NodeHandle { NodeId = nodeId, Node = variable, Validated = true };
            }
        }

        private static bool TryReadTorus(string path, string filter, out TorusValue? value)
        {
            value = null;
            try
            {
                return OpcUaValues.TryReadItem(Torus.Torus.Instance.GetData(OpcUaValues.TorusAddress(path), filter), out value);
            }
            catch (Exception)
            {
                return false;
            }
        }

        // 노드 생성 이후 TORUS가 다른 타입·모양으로 답하면 기존 노드 정의와 맞지 않으므로 오류로 본다.
        private static bool Fits(BaseDataVariableState node, TorusValue value) =>
            node.DataType == DataTypeOf(value.Kind)
            && node.ValueRank == (value.IsArray ? ValueRanks.OneDimension : ValueRanks.Scalar);

        private bool IsMonitored(NodeState node)
        {
            lock (_monitoredLock) return _monitored.ContainsValue((BaseDataVariableState)node);
        }

        private ServiceResult OnReadTorusValue(ISystemContext context, NodeState node, NumericRange indexRange,
            QualifiedName dataEncoding, ref object value, ref StatusCode statusCode, ref DateTime timestamp)
        {
            // 구독 중인 노드는 폴링 스레드가 갱신한 값을 그대로 돌려준다(읽을 때마다 TORUS를 부르지 않음).
            if (IsMonitored(node)) return ServiceResult.Good;

            var variable = (BaseDataVariableState)node;
            if (!OpcUaValues.TryParseNodePath(variable.NodeId.Identifier as string, out string path, out string filter)
                || !TryReadTorus(path, filter, out var fresh))
                return StatusCodes.BadCommunicationError;
            if (!Fits(variable, fresh!)) return StatusCodes.BadTypeMismatch;

            value = fresh!.Value;
            timestamp = DateTime.UtcNow;
            return ServiceResult.Good;
        }

        // 익명 사용자는 읽기·구독만 할 수 있다. 기본 설정이 익명 + 보안 없음이라, 이 제한이 없으면
        // 네트워크의 누구나 CNC 값을 바꿀 수 있다. 쓰기는 Username 인증으로 접속한 사용자만.
        private static bool IsAnonymous(ISystemContext context)
        {
            var identity = (context as ServerSystemContext)?.OperationContext?.UserIdentity;
            return identity == null || identity.TokenType == UserTokenType.Anonymous;
        }

        // 클라이언트가 UserAccessLevel을 읽으면 익명에게는 읽기 전용으로 알린다.
        private static ServiceResult OnReadUserAccessLevel(ISystemContext context, NodeState node, ref byte value)
        {
            if (IsAnonymous(context)) value = AccessLevels.CurrentRead;
            return ServiceResult.Good;
        }

        private ServiceResult OnWriteTorusValue(ISystemContext context, NodeState node, NumericRange indexRange,
            QualifiedName dataEncoding, ref object value, ref StatusCode statusCode, ref DateTime timestamp)
        {
            var variable = (BaseDataVariableState)node;
            string identifier = (string)variable.NodeId.Identifier;
            if (IsAnonymous(context))
            {
                _log?.Invoke($"WRITE   {identifier} rejected (anonymous is read-only)");
                return StatusCodes.BadUserAccessDenied;
            }
            if (indexRange != NumericRange.Empty) return StatusCodes.BadIndexRangeInvalid; // 부분 쓰기는 지원하지 않음

            var typeInfo = TypeInfo.IsInstanceOfDataType(value, variable.DataType, variable.ValueRank,
                context.NamespaceUris, context.TypeTable);
            if (typeInfo == null || typeInfo == TypeInfo.Unknown) return StatusCodes.BadTypeMismatch;

            // 항상 {"value":[...]} 배열로 만들어 보낸다. 빈 배열 등 TORUS로 보내면 안 되는 값은 여기서 거부.
            string? body = OpcUaValues.BuildUpdateBody(value);
            if (body == null) return StatusCodes.BadTypeMismatch;
            if (!OpcUaValues.TryParseNodePath(identifier, out string path, out string filter)) return StatusCodes.BadNodeIdUnknown;

            int status;
            try
            {
                using var doc = JsonDocument.Parse(Torus.Torus.Instance.UpdateData(OpcUaValues.TorusAddress(path), filter, body));
                status = doc.RootElement.GetProperty("status").GetInt32();
            }
            catch (Exception)
            {
                return StatusCodes.BadCommunicationError;
            }
            if (status != 0)
            {
                _log?.Invoke($"WRITE   {identifier} {body} -> TORUS 0x{status:X8} {WebServer.TorusStatusCodes.GetDescription(status)}");
                return StatusCodes.BadCommunicationError;
            }

            _log?.Invoke($"WRITE   {identifier} {body}");
            // 이 핸들러는 노드 관리자 Lock 안에서 불리므로 여기서 기다리지 않는다(기존 서버의 100ms 대기는 모든 세션을 막았다).
            // TORUS가 실제로 반영한 값은 다음 폴링이 다시 알리도록 마지막 값을 지운다.
            lock (Lock) _lastSignature.Remove(node);
            return ServiceResult.Good;
        }

        protected override void OnMonitoredItemCreated(ServerSystemContext context, NodeHandle handle, ISampledDataChangeMonitoredItem monitoredItem)
        {
            base.OnMonitoredItemCreated(context, handle, monitoredItem);
            // Value 속성 구독만 폴링한다(DisplayName 등 다른 속성 구독은 TORUS를 부를 이유가 없다).
            if (monitoredItem.AttributeId != Attributes.Value) return;
            if (handle.Node is not BaseDataVariableState variable) return;
            lock (_monitoredLock) _monitored[monitoredItem.Id] = variable;
            lock (Lock) _lastSignature.Remove(variable); // 다음 폴링에서 현재 값을 바로 알리도록
        }

        protected override void OnMonitoredItemDeleted(ServerSystemContext context, NodeHandle handle, ISampledDataChangeMonitoredItem monitoredItem)
        {
            base.OnMonitoredItemDeleted(context, handle, monitoredItem);
            bool stillMonitored;
            lock (_monitoredLock)
            {
                _monitored.Remove(monitoredItem.Id);
                stillMonitored = handle.Node is BaseDataVariableState v && _monitored.ContainsValue(v);
            }
            if (!stillMonitored) lock (Lock) _lastSignature.Remove(handle.Node);
        }

        internal int MonitoredNodeCount
        {
            get { lock (_monitoredLock) return _monitored.Values.Distinct().Count(); }
        }

        // 백그라운드 스레드라 예외가 새면 프로세스 전체가 종료되므로, 노드마다 잡아서 로그만 남긴다.
        private void PollLoop()
        {
            while (_polling)
            {
                List<BaseDataVariableState> nodes;
                lock (_monitoredLock) nodes = _monitored.Values.Distinct().ToList();
                foreach (var node in nodes)
                {
                    if (!_polling) return;
                    try
                    {
                        PollNode(node);
                    }
                    catch (Exception ex)
                    {
                        _log?.Invoke($"POLL    {node.NodeId.Identifier} failed: {ex.Message}");
                    }
                }
                Thread.Sleep(_pollIntervalMs);
            }
        }

        // TORUS에서 다시 읽어 값이나 상태가 바뀌었을 때만 구독자에게 알린다.
        private void PollNode(BaseDataVariableState node)
        {
            TorusValue? value = null;
            bool ok = OpcUaValues.TryParseNodePath(node.NodeId.Identifier as string, out string path, out string filter)
                      && TryReadTorus(path, filter, out value) && Fits(node, value!);
            string signature = ok ? OpcUaValues.Signature(value!.Value) : "\0bad";

            lock (Lock)
            {
                if (_lastSignature.TryGetValue(node, out var last) && last == signature) return;
                _lastSignature[node] = signature;
                if (ok)
                {
                    node.Value = value!.Value;
                    node.StatusCode = StatusCodes.Good;
                }
                else
                {
                    node.StatusCode = StatusCodes.BadCommunicationError;
                }
                node.Timestamp = DateTime.UtcNow;
                node.ClearChangeMasks(SystemContext, false);
            }
        }
    }
}
