using System.Runtime.CompilerServices;

// 窓口（Http）・Editor の係・テストは、芯の内側の口（列を回す・門の部品・トークン）を使う。
// ゲームの側（表と裏の葉）は、公開の口だけを使う。
[assembly: InternalsVisibleTo("Monster.Balloon.AutomataSystem.Http")]
[assembly: InternalsVisibleTo("Monster.Balloon.AutomataSystem.Editor")]
[assembly: InternalsVisibleTo("Monster.Balloon.AutomataSystem.Tests")]
