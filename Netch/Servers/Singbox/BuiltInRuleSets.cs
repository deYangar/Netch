using Netch;

namespace Netch.Servers;

/// <summary>
///     内置规则集定义（国内域名/IP 分流的数据底座，替代 1.12 起移除的 geoip/geosite 内联规则）。
///     .srs 快照随包分发到 bin\rule-set\（initial_path 离线兜底），运行期由内核按 update_interval 自动更新。
/// </summary>
public static class BuiltInRuleSets
{
    public sealed class Definition
    {
        public string Tag { get; init; } = "";
        public string Url { get; init; } = "";

        /// <summary>相对 bin 目录（sing-box 工作目录）的初始快照路径</summary>
        public string InitialPath { get; init; } = "";

        public string UpdateInterval { get; init; } = "1d";
    }

    // 源选型 2026-10-26 实测：LoyalLogic release 分支 404；SagerNet 官方 sing-geosite/sing-geoip
    // 仓库 rule-set 分支直链可用（SRS v1 魔数校验通过），且为 sing-box 作者维护，选它写死。
    // 2026-10-29 换 jsDelivr CDN（外部资源更新通道同步换）：raw.githubusercontent 直连不稳，CDN 国内可达。
    public static readonly Definition[] All =
    {
        new() { Tag = "geosite-cn", Url = "https://cdn.jsdelivr.net/gh/SagerNet/sing-geosite@rule-set/geosite-cn.srs", InitialPath = @"rule-set\geosite-cn.srs" },
        new() { Tag = "geoip-cn", Url = "https://cdn.jsdelivr.net/gh/SagerNet/sing-geoip@rule-set/geoip-cn.srs", InitialPath = @"rule-set\geoip-cn.srs" }
    };

    public static Definition? Find(string tag)
    {
        // 用户可整体停用内置规则集：关闭后 Find 视同不存在（路由校验/定义生成/tag 占用检查统一生效）
        if (!Global.Settings.UseBuiltInRuleSets)
            return null;

        // R 线单条禁用：外部资源页禁用单个内置规则集同样视同不存在，
        // 配置生成摘除定义与引用（路由规则失去该 matcher、DNS 分流不再注入），不报错
        var resource = Netch.Services.ExternalResourceService.Find($"ruleset.{tag}");
        if (resource != null && Netch.Services.ExternalResourceService.IsDisabled(resource))
            return null;

        return All.FirstOrDefault(d => d.Tag == tag);
    }

    /// <summary>tag 是否在内置注册表中（含被禁用/总开关关闭的条目；tag 占用检查与校验放行用）</summary>
    public static bool IsRegistered(string tag)
    {
        return All.Any(d => d.Tag == tag);
    }
}
