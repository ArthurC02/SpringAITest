using System.Text.Json;
using Backend.Api.Common;

namespace Backend.Api.Configuration;

/// <summary>
/// Configuration Set 的 values 逐鍵型別/範圍驗證(設計 §9 開放鍵表)。
/// 白名單以外的鍵一律拒收 — v1 只開放這七個鍵,未知鍵 = 422(不靜默吞掉、不寫入)。
/// 驗證在寫入 DB **之前**跑;任一鍵越界 → 拋 ApiException(422){FieldErrors},零副作用
/// (fieldErrors 的 key = 出錯的 values 鍵,value = 人話訊息;比照 SkillController 的 422 形狀)。
/// int 鍵要求嚴格整數(浮點/字串一律拒),float 鍵接受任意 JSON 數字。
/// </summary>
public static class ConfigurationValues
{
    // ponytail: 對話模型白名單硬寫成 litellm-config.yaml 的對話 model_name(gpt-4o-mini / mock-gpt);
    // text-embedding-3-small 是嵌入模型不列入。升級路徑 = 若 LiteLLM 路由變多,改讀 model_list 或走 env。
    public static readonly IReadOnlySet<string> AllowedModels =
        new HashSet<string>(StringComparer.Ordinal) { "gpt-4o-mini", "mock-gpt" };

    public static void Validate(IReadOnlyDictionary<string, object>? values)
    {
        if (values is null || values.Count == 0)
        {
            return; // 空 values 合法:整組回落全域預設。
        }

        var errors = new Dictionary<string, string>();
        foreach (var (key, raw) in values)
        {
            // System.Text.Json 把 Dictionary<string,object> 的值反序列化成 JsonElement;
            // 若上游改用其他型別(理論上不會),當作格式錯誤擋掉。
            if (raw is not JsonElement element)
            {
                errors[key] = $"{key} 的值格式無法解析";
                continue;
            }

            switch (key)
            {
                case "retrieval.top_k":
                    ValidateInt(element, key, 1, 50, errors);
                    break;
                case "kb_query.top_k":
                case "kb_query.max_retrieval_attempts":
                case "workflow.timeout_seconds":
                    ValidateInt(element, key, 1, null, errors);
                    break;
                case "intent.confidence_threshold":
                    ValidateFloat(element, key, 0, 1, errors);
                    break;
                case "llm.temperature":
                    ValidateFloat(element, key, 0, 2, errors);
                    break;
                case "llm.model":
                    ValidateModel(element, key, errors);
                    break;
                default:
                    errors[key] = $"未知的設定鍵：{key}";
                    break;
            }
        }

        if (errors.Count > 0)
        {
            throw new ApiException(StatusCodes.Status422UnprocessableEntity, "Configuration Set 驗證失敗")
            {
                FieldErrors = errors,
            };
        }
    }

    /// <summary>整數鍵:必須是 JSON 整數(浮點如 2.5、字串一律拒)且落在 [min, max](max 省略 = 無上限)。</summary>
    private static void ValidateInt(JsonElement el, string key, long min, long? max, Dictionary<string, string> errors)
    {
        if (el.ValueKind != JsonValueKind.Number || !el.TryGetInt64(out var v))
        {
            errors[key] = $"{key} 必須是整數";
            return;
        }

        if (v < min || (max is long m && v > m))
        {
            errors[key] = max is long mx
                ? $"{key} 必須介於 {min} 到 {mx}"
                : $"{key} 必須 ≥ {min}";
        }
    }

    /// <summary>浮點鍵:接受任意 JSON 數字(含整數字面),落在 [min, max];字串一律拒。</summary>
    private static void ValidateFloat(JsonElement el, string key, double min, double max, Dictionary<string, string> errors)
    {
        if (el.ValueKind != JsonValueKind.Number || !el.TryGetDouble(out var v))
        {
            errors[key] = $"{key} 必須是數字";
            return;
        }

        if (v < min || v > max)
        {
            errors[key] = $"{key} 必須介於 {min} 到 {max}";
        }
    }

    /// <summary>模型鍵:必須是白名單內的非空字串;未知模型/型別 → 拒。</summary>
    private static void ValidateModel(JsonElement el, string key, Dictionary<string, string> errors)
    {
        if (el.ValueKind != JsonValueKind.String)
        {
            errors[key] = $"{key} 必須是字串";
            return;
        }

        var s = el.GetString();
        if (string.IsNullOrWhiteSpace(s) || !AllowedModels.Contains(s))
        {
            errors[key] = $"{key} 不是已配置的模型：{s}";
        }
    }
}
