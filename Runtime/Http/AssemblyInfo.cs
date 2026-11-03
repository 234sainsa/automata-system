using UnityEngine.Scripting;

// ⭐ だれもこのアセンブリを参照しない（芯へは、読み込まれた時に自分を作る口を渡すだけ）。
//    ビルドで丸ごと削られないように、コードを削る係に「必ず見る」と伝える。
[assembly: AlwaysLinkAssembly]
