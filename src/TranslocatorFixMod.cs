using System.Linq;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace lionfox_fixtl
{
    public class TranslocatorFixMod : ModSystem
    {
        ICoreServerAPI? sapi;

        public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

        public override void StartServerSide(ICoreServerAPI sapi)
        {
            this.sapi = sapi;

            // /fixtl              -> clear caller's own Teleporting flag
            // /fixtl status       -> list all online players and whether their flag is stuck
            // /fixtl all          -> clear the flag on every online player (admin)
            // /fixtl player <n>   -> clear the flag on a specific player (admin)
            sapi.ChatCommands.Create("fixtl")
                .WithDescription("Clear stuck Entity.Teleporting flag on your player entity. Workaround for 'translocator particles play but no teleport' caused by an unresolved chunk-load callback inside Entity.TeleportToDouble.")
                .RequiresPrivilege(Privilege.chat)
                .HandleWith(FixSelf)
                .BeginSubCommand("status")
                    .WithDescription("List all online players and whether their Teleporting flag is currently set.")
                    .RequiresPrivilege(Privilege.chat)
                    .HandleWith(Status)
                .EndSubCommand()
                .BeginSubCommand("all")
                    .WithDescription("Clear the stuck flag on every online player.")
                    .RequiresPrivilege(Privilege.controlserver)
                    .HandleWith(FixAll)
                .EndSubCommand()
                .BeginSubCommand("player")
                    .WithDescription("Clear the stuck flag on the named player. Usage: /fixtl player <name>")
                    .RequiresPrivilege(Privilege.controlserver)
                    .WithArgs(sapi.ChatCommands.Parsers.Word("playername"))
                    .HandleWith(FixNamed)
                .EndSubCommand();
        }

        TextCommandResult FixSelf(TextCommandCallingArgs args)
        {
            if (args.Caller.Player is not IServerPlayer player || player.Entity == null)
                return TextCommandResult.Error("No player entity to inspect.");

            return ClearOne(player, "self") is { } r
                ? r
                : TextCommandResult.Success("Teleporting flag was already clear. (If a translocator is still broken for you, the cause is something else.)");
        }

        TextCommandResult FixAll(TextCommandCallingArgs args)
        {
            if (sapi == null) return TextCommandResult.Error("Server API unavailable.");

            int cleared = 0;
            int checkedCount = 0;
            var detail = new StringBuilder();

            foreach (var player in sapi.World.AllOnlinePlayers.OfType<IServerPlayer>())
            {
                if (player.Entity == null) continue;
                checkedCount++;
                if (player.Entity.Teleporting)
                {
                    player.Entity.Teleporting = false;
                    cleared++;
                    detail.AppendLine($"  cleared: {player.PlayerName} (eid {player.Entity.EntityId})");
                    sapi.Logger.Notification($"[lionfox_fixtl] Cleared stuck Teleporting flag on {player.PlayerName} (eid {player.Entity.EntityId}) via /fixtl all from {args.Caller.GetName()}.");
                }
            }

            string msg = $"Checked {checkedCount} online player(s), cleared {cleared} stuck Teleporting flag(s).";
            if (cleared > 0) msg += "\n" + detail.ToString().TrimEnd();
            return TextCommandResult.Success(msg);
        }

        TextCommandResult FixNamed(TextCommandCallingArgs args)
        {
            if (sapi == null) return TextCommandResult.Error("Server API unavailable.");

            string? name = args[0] as string;
            if (string.IsNullOrWhiteSpace(name)) return TextCommandResult.Error("Usage: /fixtl player <name>");

            var target = sapi.World.AllOnlinePlayers
                .OfType<IServerPlayer>()
                .FirstOrDefault(p => string.Equals(p.PlayerName, name, System.StringComparison.OrdinalIgnoreCase));

            if (target == null) return TextCommandResult.Error($"No online player named '{name}'.");
            if (target.Entity == null) return TextCommandResult.Error($"{target.PlayerName} has no entity.");

            return ClearOne(target, $"admin {args.Caller.GetName()}") ?? TextCommandResult.Success($"{target.PlayerName}: flag was already clear.");
        }

        TextCommandResult Status(TextCommandCallingArgs args)
        {
            if (sapi == null) return TextCommandResult.Error("Server API unavailable.");

            var sb = new StringBuilder();
            int stuck = 0;
            int total = 0;
            foreach (var player in sapi.World.AllOnlinePlayers.OfType<IServerPlayer>())
            {
                if (player.Entity == null) continue;
                total++;
                bool flag = player.Entity.Teleporting;
                if (flag) stuck++;
                sb.AppendLine($"  {player.PlayerName} (eid {player.Entity.EntityId}): Teleporting={flag}");
            }
            sb.Insert(0, $"Status: {stuck}/{total} online player(s) have Teleporting=true.\n");
            return TextCommandResult.Success(sb.ToString().TrimEnd());
        }

        TextCommandResult? ClearOne(IServerPlayer player, string source)
        {
            if (sapi == null) return null;
            if (!player.Entity.Teleporting) return null;

            player.Entity.Teleporting = false;
            sapi.Logger.Notification($"[lionfox_fixtl] Cleared stuck Teleporting flag on {player.PlayerName} (eid {player.Entity.EntityId}) via /fixtl ({source}).");
            return TextCommandResult.Success($"Cleared stuck Teleporting flag on {player.PlayerName}. Try a translocator now.");
        }
    }
}
