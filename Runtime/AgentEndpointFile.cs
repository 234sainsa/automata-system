using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;

namespace Monster.Balloon.AutomataSystem
{
    /// <summary>
    /// つなぎ先ファイル（口の番号・2 本のトークン・pid・playId・開いた力）を書く・消す。
    /// <para>
    /// Editor＝<c>&lt;プロジェクト&gt;/Library/AgentApi/endpoint.json</c>（git にも同期にも載らない）。
    /// ビルド＝<c>&lt;persistentDataPath&gt;/AgentApi/endpoint.json</c>。
    /// ⭐ 専用のつなぎ先を持つ力 1 つだけの窓口は、その力の書き付けの名前（<see cref="AgentAbilityDef.SoloEndpointFileName"/>）に書く。
    ///    ふだんの橋は endpoint.json しか読まないので、動いているほかのセッションの橋が、その窓口を拾わない。
    /// ⭐ 使う側（外の台本）は、トークンを字に直に書かず、このファイルを読む。
    /// </para>
    /// </summary>
    internal static class AgentEndpointFile
    {
        /// <summary>ふだんのつなぎ先の名前。</summary>
        public const string DefaultFileName = "endpoint.json";

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        /// <summary>この力の組で開いた窓口のつなぎ先の道。</summary>
        public static string PathFor(AgentAbilities abilities)
        {
            string root = Application.isEditor
                ? Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "Library")
                : Application.persistentDataPath;
            return Path.Combine(root, "AgentApi", FileNameFor(abilities));
        }

        /// <summary>専用のつなぎ先を持つ力 1 つだけならその名前、ほかはふだんの名前（<see cref="AgentAbilityCatalog.EndpointFileNameFor"/>）。</summary>
        internal static string FileNameFor(AgentAbilities abilities) => AgentAbilityCatalog.Current.EndpointFileNameFor(abilities);

        /// <summary>
        /// この力の組では使わないつなぎ先の道の全部（前の Play の残りを消すため）。
        /// ⭐ 残りがあると、専用の橋が、もう居ない窓口を読んで迷う。
        /// </summary>
        internal static List<string> OtherPathsFor(AgentAbilities abilities)
        {
            string used = FileNameFor(abilities);
            string dir = Path.GetDirectoryName(PathFor(abilities)) ?? ".";
            var others = new List<string>();
            foreach (string name in AgentAbilityCatalog.Current.EndpointFileNames())
            {
                if (!string.Equals(name, used, StringComparison.OrdinalIgnoreCase))
                {
                    others.Add(Path.Combine(dir, name));
                }
            }

            return others;
        }

        public static void Write(
            string path,
            int port,
            string surfaceToken,
            string backToken,
            int processId,
            string playId,
            string openWord,
            AgentAbilities abilities)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var sw = new StringWriter(CultureInfo.InvariantCulture);
            using (var w = new JsonTextWriter(sw) { Formatting = Formatting.Indented })
            {
                w.WriteStartObject();
                w.WritePropertyName("api");
                w.WriteValue(ApiVersion.Current.ToString());
                w.WritePropertyName("port");
                w.WriteValue(port);
                w.WritePropertyName("base");
                w.WriteValue("http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture));
                w.WritePropertyName("surfaceToken");
                w.WriteValue(surfaceToken);
                if (backToken != null)
                {
                    w.WritePropertyName("backToken");
                    w.WriteValue(backToken);
                }
                w.WritePropertyName("pid");
                w.WriteValue(processId);
                w.WritePropertyName("playId");
                w.WriteValue(playId);
                w.WritePropertyName("open");
                w.WriteValue(openWord);
                w.WritePropertyName("abilities");
                w.WriteStartArray();
                foreach (string ability in abilities.ToWireList())
                {
                    w.WriteValue(ability);
                }

                w.WriteEndArray();
                w.WritePropertyName("startedUtc");
                w.WriteValue(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                w.WriteEndObject();
            }

            File.WriteAllText(path, sw.ToString(), Utf8NoBom);
        }

        public static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception e)
            {
                // 消せなくても次の Play で書き直す（古いトークンはもう門を通らない）。
                Debug.LogWarning("[AgentApi] つなぎ先ファイルを消せなかった: " + e.Message);
            }
        }
    }
}
