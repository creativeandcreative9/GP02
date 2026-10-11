using System.Runtime.CompilerServices;

// 画面に依存しない内部の処理（プレビューの救済など）を、EditMode テストから確かめるため
[assembly: InternalsVisibleTo("GP02.Tests.Editor")]
