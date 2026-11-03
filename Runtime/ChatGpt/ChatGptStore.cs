using System;
using System.IO;
using Newtonsoft.Json;

namespace Monster.Balloon.AutomataSystem.ChatGpt
{
    /// <summary>
    /// 保存するログインの中身（1 つのファイル）。
    /// ⚠ メールや名前など、人が分かる物は持たない。持つのは端末の印・発行された client_id・トークン・scope・期限・sub・プランの種類だけ。
    /// ⚠ id_token は持たない（中にメールが入るため・2 回目の authorize に id_token_hint も付けないので要らない）。
    /// </summary>
    internal sealed class ChatGptSaved
    {
        [JsonProperty("version")] public int Version = 1;
        [JsonProperty("ext_agent_host_id")] public string HostId;
        [JsonProperty("client_id")] public string ClientId;
        [JsonProperty("access_token")] public string AccessToken;
        [JsonProperty("refresh_token")] public string RefreshToken;
        [JsonProperty("scope")] public string Scope;
        [JsonProperty("expires_at")] public long ExpiresAtUnix;
        [JsonProperty("earliest_refresh_at")] public long EarliestRefreshAtUnix;
        [JsonProperty("sub")] public string Subject;
        [JsonProperty("plan_type")] public string PlanType;
        [JsonProperty("plan_notice_pending")] public bool PlanNoticePending;

        /// <summary>トークンが一式そろっているか。</summary>
        [JsonIgnore]
        public bool HasTokens => !string.IsNullOrEmpty(this.AccessToken) && !string.IsNullOrEmpty(this.RefreshToken);

        /// <summary>トークンだけを消す（端末の印と client_id は残す＝次のログインで同じ client_id を使う）。</summary>
        public void ForgetTokens()
        {
            this.AccessToken = null;
            this.RefreshToken = null;
            this.Scope = null;
            this.ExpiresAtUnix = 0;
            this.EarliestRefreshAtUnix = 0;
            this.Subject = null;
            this.PlanType = null;
        }
    }

    /// <summary>
    /// ログインの中身をファイルへ読み書きする係。
    /// ⭐ 書く時は一時ファイルに書いてから置き換える（書きかけで落ちても、前の中身が残る）。
    /// ⚠ 例外の字にも中身を入れない（パスと訳だけ）。
    /// </summary>
    internal sealed class ChatGptStore
    {
        internal const string FileName = "account.json";

        public ChatGptStore(string directory)
        {
            this.Directory = directory ?? throw new ArgumentNullException(nameof(directory));
            this.FilePath = Path.Combine(directory, FileName);
        }

        public string Directory { get; }

        public string FilePath { get; }

        /// <summary>
        /// 保存を読む。無い・壊れている時は、新しい端末の印だけを持つ中身を作って保存する。
        /// </summary>
        public ChatGptSaved LoadOrCreate()
        {
            ChatGptSaved saved = this.TryLoad();
            if (saved != null && !string.IsNullOrEmpty(saved.HostId)) return saved;

            // 端末の印は、入れた物ごとに 1 つ。人が分かる物にしない＝乱数の UUID にする。
            saved = saved ?? new ChatGptSaved();
            saved.HostId = "urn:uuid:" + Guid.NewGuid().ToString("D");
            this.Save(saved);
            return saved;
        }

        public void Save(ChatGptSaved saved)
        {
            System.IO.Directory.CreateDirectory(this.Directory);
            string json = JsonConvert.SerializeObject(saved, Formatting.Indented);
            string temp = this.FilePath + ".tmp";
            File.WriteAllText(temp, json);
            if (File.Exists(this.FilePath))
            {
                File.Replace(temp, this.FilePath, null);
            }
            else
            {
                File.Move(temp, this.FilePath);
            }
        }

        private ChatGptSaved TryLoad()
        {
            if (!File.Exists(this.FilePath)) return null;
            try
            {
                return JsonConvert.DeserializeObject<ChatGptSaved>(File.ReadAllText(this.FilePath));
            }
            catch (JsonException)
            {
                // 壊れた保存は読まない（中身を字に出さない）。新しい中身で上書きされる。
                return null;
            }
        }
    }
}
