using System.Windows;
using System.Windows.Controls;
using NetCat.Core;

namespace NetCat.UI;
internal static class RuleEditor
{
    public static EditorDialog Create(Window owner, RoutingRule rule, Action<RoutingRule> save)
    {
        var d=new EditorDialog(owner,"Правило маршрутизации");
        int Group(RuleKind kind)=>kind is RuleKind.Domain or RuleKind.ExactDomain ? 0 : kind==RuleKind.IpCidr ? 2 : kind==RuleKind.GeoSite ? 3 : kind==RuleKind.GeoIp ? 4 : 1;
        var groups=new[]{"Домен","Приложение","IP / подсеть","GeoSite · группа доменов","GeoIP · страна / сеть"};
        var basic=d.Choice("Что направить",groups,groups[Group(rule.Kind)]);
        var value=d.Text("Домен, приложение, подсеть или категория геоданных",rule.Value);
        d.Note("GeoSite: youtube, category-ru, category-ads-all. GeoIP: ru, telegram. Также можно вставить geosite:youtube или geoip:ru. Базы обновляются на вкладке «Модули».");
        var target=d.Choice("Куда направлять",Enum.GetValues<RouteTarget>(),rule.Target);
        ComboBox kind=null!,network=null!; TextBox port=null!; CheckBox enabled=null!;
        d.Advanced(()=>
        {
            kind=d.Choice("Точный тип совпадения",Enum.GetValues<RuleKind>(),rule.Kind);
            network=d.Choice("Транспорт (пусто — любой)",new[]{"","tcp","udp"},rule.Network);
            port=d.Text("Порт назначения (пусто — любой)",rule.Port);
            enabled=d.Check("Правило включено",rule.Enabled);
            d.Note("Обычно транспорт и порт оставляют пустыми. Если указаны оба, должны совпасть оба условия. «Домен и поддомены» включает вложенные адреса; «Точный домен» — только введённый адрес.");
        },rule.Network.Length>0 || rule.Port.Length>0 || rule.Kind==RuleKind.ExactDomain || !rule.Enabled);
        bool syncing=false;
        basic.SelectionChanged+=(_,_)=>{if(syncing)return; syncing=true; kind.SelectedItem=basic.SelectedIndex switch {1=>RuleKind.Process,2=>RuleKind.IpCidr,3=>RuleKind.GeoSite,4=>RuleKind.GeoIp,_=>RuleKind.Domain}; syncing=false;};
        kind.SelectionChanged+=(_,_)=>{if(syncing)return; syncing=true; basic.SelectedIndex=Group((RuleKind)kind.SelectedItem); syncing=false;};
        d.OnAccept=()=>
        {
            rule.Kind=(RuleKind)kind.SelectedItem; rule.Value=value.Text;
            if(rule.Kind==RuleKind.Process && (rule.Value.Contains('\\') || Path.IsPathRooted(rule.Value))) rule.Kind=RuleKind.ExecutablePath;
            rule.Target=(RouteTarget)target.SelectedItem; rule.Network=(string)network.SelectedItem; rule.Port=port.Text.Trim(); rule.Enabled=enabled.IsChecked==true;
            RuleValidation.Validate(rule);
            if (rule.Kind is RuleKind.GeoSite or RuleKind.GeoIp && owner is MainWindow window)
                _ = NetCat.Engine.GeodataCache.Shared.Get(NetCat.Engine.Geodata.FilePath(window.VM.Bin, rule.Kind), rule.Kind == RuleKind.GeoIp).Match(rule.Value);
            save(rule); return Task.FromResult(true);
        };
        return d;
    }
}
