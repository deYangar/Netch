using Netch.Localization;
using Netch.Models.Modes;

namespace Netch.ViewModels;

/// <summary>维度下拉项，模型枚举不侵入持久化 JSON</summary>
public sealed record DimensionChoice(RouteRuleDimension Kind, string Text)
{
    public override string ToString() => Text;
}
