using Aqua.BTCPayPlugin.Services;
using BTCPayServer.Plugins.Template;

namespace Aqua.BTCPayPlugin;

public class Plugin : BaseBTCPayServerPlugin
{
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    {
        new IBTCPayServerPlugin.PluginDependency { Identifier = nameof(BTCPayServer), Condition = ">=1.12.0" }
    };

    public override void Execute(IServiceCollection services)
    {
        services.AddSingleton<IUIExtension>(new UIExtension("TemplatePluginHeaderNav", "header-nav"));
        services.AddHostedService<ApplicationPartsLogger>();
        services.AddHostedService<PluginMigrationRunner>();
        services.AddSingleton<MyPluginService>();
        services.AddSingleton<MyPluginDbContextFactory>();
        services.AddDbContext<MyPluginDbContext>((provider, o) =>
        {
            var factory = provider.GetRequiredService<MyPluginDbContextFactory>();
            factory.ConfigureBuilder(o);
        });
    }
}
