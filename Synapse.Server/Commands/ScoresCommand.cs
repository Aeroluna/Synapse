﻿using Microsoft.Extensions.Logging;
using Synapse.Networking.Models;
using Synapse.Server.Clients;
using Synapse.Server.Extras;
using Synapse.Server.Models;
using Synapse.Server.Services;

namespace Synapse.Server.Commands;

public class ScoresCommand(
    ILogger<ScoresCommand> log,
    ILeaderboardService leaderboardService,
    IMapService mapService,
    IEventService eventService,
    IListenerService listenerService,
    IBackupService backupService,
    IListingService listingService)
{
    [Command("scores", Permission.Coordinator)]
    public void Scores(IClient client, string arguments)
    {
        arguments.SplitCommand(out string subCommand, out string subArguments);
        switch (subCommand)
        {
            case "test":
                subArguments.TooMany();
                leaderboardService.GenerateTestScores();
                client.SendPriorityServerMessage("Generated test scores");

                break;

            case "refresh":
            {
                subArguments.SplitCommand(out string mapIndex);
                int mapIndexInt;
                if (string.IsNullOrWhiteSpace(mapIndex))
                {
                    mapIndexInt = mapService.Index;
                }
                else if (!int.TryParse(mapIndex, out mapIndexInt))
                {
                    throw new CommandParseException(mapIndex);
                }

                if (mapIndexInt < 0 ||
                    mapIndexInt >= mapService.MapCount)
                {
                    throw new CommandInvalidMapIndexException(mapIndexInt, mapService.MapCount);
                }

                leaderboardService.BroadcastLeaderboard(mapIndexInt);
                client.SendPriorityServerMessage("Refreshed leaderboards for [{Map} ({Index}))]",
                    mapService.Maps[mapIndexInt].Name,
                    mapIndexInt);

                break;
            }

            case "remove":
            {
                int mapIndexInt;
                string flags = subArguments.GetFlags(out string extra);
                extra.SplitCommand(out string mapIndex, out string subSubArguments);
                subSubArguments.SplitCommand(out string subSubCommand, out string subSubSubArguments);

                if (!int.TryParse(subSubCommand, out int divisionInt))
                {
                    throw new CommandParseException(subSubCommand);
                }

                subSubSubArguments.SplitCommand(out string subSubSubCommand, out string subSubSubSubArguments);
                string id = subSubSubCommand.Unwrap();

                string mapIndexString = subSubSubSubArguments.Unwrap();
                if (string.IsNullOrWhiteSpace(mapIndexString))
                {
                    mapIndexString = mapIndex;
                    mapIndexInt = mapService.Index;
                }
                else if (!int.TryParse(subArguments, out mapIndexInt))
                {
                    throw new CommandParseException(subArguments);
                }

                if (mapIndexInt < 0 ||
                    mapIndexInt >= mapService.MapCount)
                {
                    throw new CommandInvalidMapIndexException(mapIndexInt, mapService.MapCount);
                }

                SavedScore score = leaderboardService
                    .AllScores[divisionInt][mapIndexInt]
                    .ScanQuery(
                        id,
                        flags.Contains('i') ? n => n.Id : n => n.Username);
                client.LogAndSend(
                    log,
                    "Removed score [{Score}] from [{Map} ({Index}))] ({Division})",
                    score,
                    mapService.Maps[mapIndexInt].Name,
                    mapIndexInt,
                    listingService.GetDivisionName(divisionInt));
                leaderboardService.RemoveScore(divisionInt, mapIndexInt, score);
                if (listenerService.Clients.TryGetValue(score.Id, out IClient? target))
                {
                    eventService.SendStatus(target);
                }

                break;
            }

            // TODO: add a "are you sure" prompt
            case "drop":
            {
                subArguments.SplitCommand(out string divisionIndex, out string mapIndex);
                mapIndex = mapIndex.Unwrap();

                if (!int.TryParse(divisionIndex, out int divisionInt))
                {
                    throw new CommandParseException(divisionIndex);
                }

                int mapIndexInt;
                if (string.IsNullOrWhiteSpace(mapIndex))
                {
                    mapIndexInt = mapService.Index;
                }
                else if (!int.TryParse(mapIndex, out mapIndexInt))
                {
                    throw new CommandParseException(mapIndex);
                }

                if (mapIndexInt < 0 ||
                    mapIndexInt >= mapService.MapCount)
                {
                    throw new CommandInvalidMapIndexException(mapIndexInt, mapService.MapCount);
                }

                int scoresCount = leaderboardService.AllScores[divisionInt][mapIndexInt].Count;
                leaderboardService.DropScores(divisionInt, mapIndexInt);
                client.LogAndSend(
                    log,
                    "Removed [{ScoreCount}] score(s) from [{Map} ({Index}))] ({Division})",
                    scoresCount,
                    mapService.Maps[mapIndexInt].Name,
                    mapIndexInt,
                    listingService.GetDivisionName(divisionInt));
                eventService.UpdateStatus(false);

                break;
            }

            case "resubmit":
            {
                subArguments.SplitCommand(out string mapIndex);
                mapIndex.TooMany();
                if (string.IsNullOrWhiteSpace(subArguments) ||
                    !int.TryParse(subArguments, out int mapIndexInt))
                {
                    throw new CommandParseException(subArguments);
                }

                if (mapIndexInt < 0 ||
                    mapIndexInt >= mapService.MapCount)
                {
                    throw new CommandInvalidMapIndexException(mapIndexInt, mapService.MapCount);
                }

                _ = ResubmitScores(client, mapIndexInt, mapService.Index);
            }

                break;

            case "list":
            {
                int mapIndexInt;
                string flags = subArguments.GetFlags(out string extra);
                extra.SplitCommand(out string subSubCommand, out string subSubSubArguments);

                if (!int.TryParse(subSubCommand, out int divisionInt))
                {
                    throw new CommandParseException(subSubCommand);
                }

                string id = subSubSubArguments.Unwrap();
                if (string.IsNullOrWhiteSpace(id))
                {
                    mapIndexInt = mapService.Index;
                }
                else if (!int.TryParse(id, out mapIndexInt))
                {
                    throw new CommandParseException(id);
                }

                if (mapIndexInt < 0 ||
                    mapIndexInt >= mapService.MapCount)
                {
                    throw new CommandInvalidMapIndexException(mapIndexInt, mapService.MapCount);
                }

                string map = mapService.Maps[mapIndexInt].Name;
                string division = listingService.GetDivisionName(divisionInt);
                IReadOnlyList<SavedScore> scores = leaderboardService.AllScores[divisionInt][mapIndexInt];
                if (scores.Count > 0)
                {
                    client.SendPriorityServerMessage("[{Map} ({Index}))] ({Division}) has {ScoresCount} scores",
                        map,
                        mapIndexInt,
                        division,
                    scores.Count);
                }
                else
                {
                    client.SendPriorityServerMessage("No scores currently submitted for [{Map} ({Index}))] ({Division})",
                        map,
                        mapIndexInt,
                        division);
                }

                if (flags.Contains('v'))
                {
                    int limit = flags.Contains('e') ? 100 : 20;
                    IEnumerable<SavedScore> limitedScores = scores.Take(limit);
                    string scoresMessage = string.Join(", ", limitedScores.Select(n => n.ToString()));
                    if (scores.Count > limit)
                    {
                        scoresMessage += ", ...";
                    }

                    client.SendPriorityServerMessage("{VerboseScores}", scoresMessage);
                }

                break;
            }

            case "backup":
                Backup(subArguments);

                break;

            default:
                throw new CommandUnrecognizedSubcommandException("scores", subCommand);
        }
    }

    private void Backup(string arguments)
    {
        arguments.SplitCommand(out string subCommand);
        switch (subCommand)
        {
            case "reload":
                backupService.LoadBackups();

                break;

            default:
                throw new CommandUnrecognizedSubcommandException("backup", subCommand);
        }
    }

    private async Task ResubmitScores(IClient client, int start, int end)
    {
        for (int i = start; i < end; i++)
        {
            await leaderboardService.SubmitTournamentScores(i);
        }

        int count = end - start;
        Map[] dest = new Map[count];
        Array.Copy(mapService.Maps.ToArray(), start, dest, 0, count);
        string affected = string.Join(", ", dest.Select(n => n.Name));
        client.LogAndSend(log, "Resubmitted scores for [{Map}]", affected);
    }
}
