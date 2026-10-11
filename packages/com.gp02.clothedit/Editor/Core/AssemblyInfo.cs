using System.Runtime.CompilerServices;

// 色分けや段階の処理など、内部の部品をクラウドの自動テストで直接確かめるため
[assembly: InternalsVisibleTo("Core.Tests")]
