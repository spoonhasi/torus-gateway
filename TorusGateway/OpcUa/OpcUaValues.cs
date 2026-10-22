using System.IO;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace TorusGateway.OpcUa
{
    // TORUS datatype의 기본 종류. OPC UA DataType(String/Int64/Double/Boolean)으로 매핑된다.
    internal enum TorusValueKind { String, Long, Double, Boolean }

    // TORUS Item 하나를 OPC UA 값으로 바꾼 결과. IsArray면 Value는 string[]/long[]/double[]/bool[].
    internal sealed record TorusValue(TorusValueKind Kind, bool IsArray, object Value);

    // NodeId ↔ TORUS 주소, TORUS Item ↔ OPC UA 값 변환 (SDK 비의존 순수 함수, 테스트 대상).
    internal static class OpcUaValues
    {
        internal const string RootPath = "/machine";

        // 쓰기 body를 TORUS로 보낼 때 한글 등은 그대로 두고 따옴표·제어문자만 이스케이프한다.
        private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        // NodeId 문자열 식별자를 TORUS 경로와 필터로 나눈다. 규칙은 HTTP와 같다:
        // "/machine/channel/axis/machinePosition?machine=1&channel=1&axis=1" (TORUS 주소에서 "data:/"를 뗀 것).
        // 필터는 반드시 있어야 한다 — TORUS 데이터 모델은 machine 필터가 필요하고, 경로만 있는 식별자는
        // 같은 경로의 폴더 NodeId와 겹치기 때문이다.
        internal static bool TryParseNodePath(string? identifier, out string path, out string filter)
        {
            path = "";
            filter = "";
            if (string.IsNullOrEmpty(identifier) || identifier.Trim().Length != identifier.Length) return false;

            int q = identifier.IndexOf('?');
            if (q <= 0 || q == identifier.Length - 1) return false;
            string p = identifier.Substring(0, q);
            if (!p.StartsWith(RootPath + "/", StringComparison.Ordinal) || p.EndsWith('/') || p.Contains("//")) return false;

            path = p;
            filter = identifier.Substring(q + 1);
            return true;
        }

        internal static string TorusAddress(string path) => "data:/" + path;

        // "/machine/channel/axis" → ["/machine/channel", "/machine/channel/axis"] (루트 아래 폴더 경로들).
        internal static IReadOnlyList<string> FolderPathsBelowRoot(string path)
        {
            var result = new List<string>();
            for (int i = path.IndexOf('/', RootPath.Length + 1); i > 0; i = path.IndexOf('/', i + 1))
                result.Add(path.Substring(0, i));
            result.Add(path);
            return result;
        }

        // TORUS Item JSON을 OPC UA 값으로 바꾼다. status가 0이 아니거나 형식이 맞지 않으면 false.
        // 배열 여부: datatype이 list*이면 항상 배열, 아니면 원소가 2개 이상일 때만 배열.
        internal static bool TryReadItem(string itemJson, out TorusValue? value)
        {
            value = null;
            try
            {
                using var doc = JsonDocument.Parse(itemJson);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return false;
                if (root.TryGetProperty("status", out var st)
                    && !(st.ValueKind == JsonValueKind.Number && st.TryGetInt32(out int s) && s == 0)) return false;
                if (!root.TryGetProperty("datatype", out var dt) || dt.ValueKind != JsonValueKind.String) return false;
                if (!root.TryGetProperty("value", out var v) || v.ValueKind != JsonValueKind.Array) return false;

                string type = dt.GetString() ?? "";
                bool isList = type.StartsWith("list", StringComparison.Ordinal);
                TorusValueKind kind;
                switch (isList ? type.Substring(4) : type)
                {
                    case "string": kind = TorusValueKind.String; break;
                    case "long": kind = TorusValueKind.Long; break;
                    case "double": kind = TorusValueKind.Double; break;
                    case "boolean": kind = TorusValueKind.Boolean; break;
                    default: return false;
                }

                int n = v.GetArrayLength();
                if (!isList && n == 0) return false;
                bool asArray = isList || n > 1;

                Array items = kind switch
                {
                    TorusValueKind.String => new string[n],
                    TorusValueKind.Long => new long[n],
                    TorusValueKind.Double => new double[n],
                    _ => new bool[n],
                };
                int i = 0;
                foreach (var e in v.EnumerateArray())
                {
                    switch (kind)
                    {
                        case TorusValueKind.String:
                            // 문자열이 아닌 원소(객체 등)는 JSON 원문 그대로.
                            ((string[])items)[i] = e.ValueKind == JsonValueKind.String ? e.GetString()! : e.GetRawText();
                            break;
                        case TorusValueKind.Long:
                            if (e.ValueKind != JsonValueKind.Number || !e.TryGetInt64(out long l)) return false;
                            ((long[])items)[i] = l;
                            break;
                        case TorusValueKind.Double:
                            if (e.ValueKind != JsonValueKind.Number) return false;
                            ((double[])items)[i] = e.GetDouble();
                            break;
                        default:
                            if (e.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
                            ((bool[])items)[i] = e.GetBoolean();
                            break;
                    }
                    i++;
                }

                value = new TorusValue(kind, asArray, asArray ? items : items.GetValue(0)!);
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        // OPC UA에서 쓴 값을 TORUS updateData body로 만든다. 항상 {"value":[...]} 배열 형태다.
        // (TORUS v2.3.3은 value가 배열이 아니면 통신 매니저가 크래시한다.)
        // 지원하지 않는 타입, 빈 배열, 유한하지 않은 실수는 null — TORUS로 보내지 않는다.
        internal static string? BuildUpdateBody(object? value)
        {
            if (value == null) return null;
            var items = value is Array a ? a.Cast<object?>().ToList() : new List<object?> { value };
            if (items.Count == 0) return null;

            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, WriterOptions))
            {
                w.WriteStartObject();
                w.WriteStartArray("value");
                foreach (var item in items)
                {
                    switch (item)
                    {
                        case string s: w.WriteStringValue(s); break;
                        case bool b: w.WriteBooleanValue(b); break;
                        case long l: w.WriteNumberValue(l); break;
                        case double d when double.IsFinite(d): w.WriteNumberValue(d); break;
                        default: return null;
                    }
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        // 값이 바뀌었는지 비교하기 위한 문자열 표현(문화권 무관).
        internal static string Signature(object value) => value switch
        {
            Array arr => string.Join("\u001f", arr.Cast<object>().Select(Scalar)),
            _ => Scalar(value),
        };

        private static string Scalar(object o) => o switch
        {
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            bool b => b ? "true" : "false",
            _ => Convert.ToString(o, CultureInfo.InvariantCulture) ?? "",
        };
    }
}
