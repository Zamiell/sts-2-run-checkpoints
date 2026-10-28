using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Saves;

namespace RunCheckpoints;

internal static class CheckpointPresentation
{
    public static SerializableRun Parse(byte[] bytes)
    {
        var result = JsonSerializationUtility.FromJson<SerializableRun>(System.Text.Encoding.UTF8.GetString(bytes));
        if (!result.Success || result.SaveData == null)
        {
            throw new InvalidDataException($"Cannot read checkpoint: {result.Status}: {result.ErrorMessage}");
        }
        var save = result.SaveData;
        if (save.Players.Count != 1 || save.CurrentActIndex < 0 || save.CurrentActIndex >= save.Acts.Count)
        {
            throw new InvalidDataException("Checkpoint is not a valid singleplayer run.");
        }
        return save;
    }

    public static string Name(string table, string? id)
    {
        if (id == null)
        {
            throw new InvalidDataException($"Missing model ID for {table}.");
        }
        var entry = id[(id.IndexOf('.') + 1)..];
        return new LocString(table, entry + ".title").GetFormattedText();
    }

    public static string Header(SerializableRun save) =>
        $"Act {save.CurrentActIndex + 1}: {Name("acts", save.Acts[save.CurrentActIndex].Id?.ToString())}"
        + $"  |  {Name("characters", save.Players[0].CharacterId?.ToString())}  |  A{save.Ascension}"
        + $"\nSeed: {save.SerializableRng.Seed}";

    public static string Location(SerializableRun save)
    {
        if (save.VisitedMapCoords.Count == 0)
        {
            return $"Act {save.CurrentActIndex + 1} - Map (before choosing a room)";
        }
        var coord = save.VisitedMapCoords[^1];
        var prefix = $"Act {save.CurrentActIndex + 1}, Floor {coord.row + 1} - ";
        if (save.PreFinishedRoom is { } room)
        {
            return prefix + (room.IsPreFinished && room.RoomType is RoomType.Monster or RoomType.Elite or RoomType.Boss
                ? "After combat / rewards" : $"Saved {room.RoomType} room");
        }

        var map = save.Acts[save.CurrentActIndex].SavedMap;
        var point = map?.Points.FirstOrDefault(item => item.Coord == coord)
            ?? (map?.BossPoint.Coord == coord ? map.BossPoint : null)
            ?? (map?.SecondBossPoint?.Coord == coord ? map.SecondBossPoint : null)
            ?? (map?.StartingPoint.Coord == coord ? map.StartingPoint : null);
        return prefix + (point?.PointType switch
        {
            MapPointType.Monster => "Before combat",
            MapPointType.Elite => "Before elite",
            MapPointType.Boss => "Before boss",
            MapPointType.RestSite => "Before rest site",
            MapPointType.Shop => "Before shop",
            MapPointType.Treasure => "Before treasure",
            MapPointType.Ancient => "Before Ancient",
            MapPointType.Unknown => "Before unknown room (resolved from saved RNG)",
            _ => "Before entering room"
        });
    }

    public static string Details(SerializableRun save, CheckpointInfo checkpoint)
    {
        var player = save.Players[0];
        var deck = player.Deck.Select(card => Name("cards", card.Id?.ToString())
            + (card.CurrentUpgradeLevel > 0 ? $" +{card.CurrentUpgradeLevel}" : ""));
        var relics = player.Relics.Select(relic => Name("relics", relic.Id?.ToString()));
        return $"{Header(save)}\n\n{Location(save)}\nHP: {player.CurrentHp}/{player.MaxHp}    Gold: {player.Gold}"
            + $"\nSaved: {checkpoint.SavedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
            + $"\n{checkpoint.Label}\n\nDeck ({player.Deck.Count}):\n{string.Join("\n", deck)}"
            + $"\n\nRelics ({player.Relics.Count}):\n{string.Join("\n", relics)}";
    }
}
