﻿using ArkBot.Commands;
using ArkBot.Data;
using ArkBot.Database;
using ArkBot.OpenID;
using ArkBot.Extensions;
using Discord;
using Discord.WebSocket;
using Discord.Commands;
using Google.Apis.Urlshortener.v1;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using ArkBot.Database.Model;
using ArkBot.Helpers;
using Autofac;
using log4net;
using System.Data.Entity.Core.Objects;
using System.Reflection;
using ArkBot.ViewModel;
using ArkBot.Ark;
using ArkBot.Discord.Command;
using ArkBot.Voting;
using Discord.WebSocket;
using RazorEngine.Compilation.ImpromptuInterface;
using VDS.Common.Collections.Enumerations;

namespace ArkBot.Discord
{
    public class ArkDiscordBot : IDisposable
    {
        private DiscordSocketClient _discord;
        private CommandService _commands;
        private IServiceProvider _serviceProvider;
        private IConfig _config;
        private IConstants _constants;
        private IBarebonesSteamOpenId _openId;
        private EfDatabaseContextFactory _databaseContextFactory;
        private ILifetimeScope _scope;
        private ArkContextManager _contextManager;
        private VotingManager _votingManager;

        private bool _wasRestarted;
        private List<ulong> _wasRestartedServersNotified = new List<ulong>();

        public ArkDiscordBot(
            DiscordSocketClient discord,
            CommandService commands,
            IServiceProvider serviceProvider,
            IConfig config, 
            IConstants constants, 
            IBarebonesSteamOpenId openId, 
            EfDatabaseContextFactory databaseContextFactory, 
            ILifetimeScope scope,
            ArkContextManager contextManager,
            VotingManager votingManager)
        {
            _discord = discord;
            _commands = commands;
            _serviceProvider = serviceProvider;
            _config = config;
            _constants = constants;
            _databaseContextFactory = databaseContextFactory;
            _openId = openId;
            _openId.SteamOpenIdCallback += _openId_SteamOpenIdCallback;
            _scope = scope;
            _contextManager = contextManager;
            _votingManager = votingManager;

            //_context.Updated += _context_Updated;

            _discord.GuildAvailable += DiscordOnGuildAvailable;
            _discord.MessageReceived += HandleCommandAsync;

            var args = Environment.GetCommandLineArgs();
            if (args != null && args.Contains("/restart", StringComparer.OrdinalIgnoreCase))
            {
                _wasRestarted = true;
            }
        }

        private async Task DiscordOnGuildAvailable(SocketGuild socketGuild)
        {
            if (_wasRestarted && socketGuild != null && !string.IsNullOrWhiteSpace(_config.AnnouncementChannel) && !_wasRestartedServersNotified.Contains(socketGuild.Id))
            {
                try
                {
                    _wasRestartedServersNotified.Add(socketGuild.Id);
                    var channel = socketGuild.TextChannels.FirstOrDefault(y => _config.AnnouncementChannel.Equals(y.Name, StringComparison.OrdinalIgnoreCase));
                    if (channel != null) await channel.SendMessageAsync("**I have automatically restarted due to previous unexpected shutdown!**");
                }
                catch (Exception ex) { /*ignore exceptions */ }
            }

            await UpdateNicknamesAndRoles(socketGuild);
        }

        private async Task HandleCommandAsync(SocketMessage messageParam)
        {
            var message = messageParam as SocketUserMessage;
            if (message == null) return;

            var argPos = 0;
            if (!(message.HasCharPrefix('!', ref argPos) || message.HasMentionPrefix(_discord.CurrentUser, ref argPos))) return;

            var context = new SocketCommandContext(_discord, message);

            var result = _commands.Search(context, argPos);
            if (result.IsSuccess && result.Commands.Count > 0)
            {
                if (result.Commands.Count > 1)
                {
                    Logging.Log($"Multiple commands registered for '{message.Content.Substring(argPos)}'! Skipping!", GetType(), LogLevel.WARN);
                    return;
                }

                var cm = result.Commands.First();
                var iCommand = cm.Command;
                var iModule = iCommand.Module;
                var isHidden = CommandHiddenAttribute.IsHidden(iModule.Attributes, iCommand.Attributes);

                var preconditions = await cm.CheckPreconditionsAsync(context, _serviceProvider);
                if (!preconditions.IsSuccess) return;

                var parseResult = await cm.ParseAsync(context, result, preconditions, _serviceProvider);
                if (!parseResult.IsSuccess) return;

                var commandResult = await cm.ExecuteAsync(context, parseResult, _serviceProvider);
                if (commandResult.IsSuccess)
                {
                    if (isHidden) return;

                    var sb = new StringBuilder();
                    sb.AppendLine($@"""!{message.Content.Substring(argPos)}"" command successful!");
                    Logging.Log(sb.ToString(), GetType(), LogLevel.INFO);
                }
                else
                {
                    if (isHidden || (preconditions.Error.HasValue && preconditions.Error.Value == CommandError.UnmetPrecondition)) return;

                    //if there is an exception log all information pertaining to it so that we can possibly fix it in the future
                    var exception = commandResult is ExecuteResult ? ((ExecuteResult)commandResult).Exception : null;
                    if (exception != null)
                    { 
                        var errorMessage = $@"""!{message.Content.Substring(argPos)}"" command error...";

                        Logging.LogException(errorMessage, exception, GetType(), LogLevel.ERROR, ExceptionLevel.Unhandled);
                    }
                }
            }

            //var result = await _commands.ExecuteAsync(context, argPos, _serviceProvider);
        }

        /// <summary>
        /// All context data have been updated (occurs on start and when a savefile change have been handled)
        /// </summary>
        private async void _context_Updated(object sender, EventArgs e)
        {
            //on the first update triggered on start, servers are not yet connected so this code will not run.
            await UpdateNicknamesAndRoles();
        }

        private async Task UpdateNicknamesAndRoles(SocketGuild _socketGuild = null)
        {
            //try
            //{
            //    //change nicknames, add/remove from ark-role
            //    Database.Model.User[] linkedusers = null;
            //    using (var db = _databaseContextFactory.Create())
            //    {
            //        linkedusers = db.Users.Where(x => !x.Unlinked).ToArray();
            //    }

            //    foreach (var server in _discord.Servers)
            //    {
            //        if (_server != null && server.Id != _server.Id) continue;

            //        var role = server.FindRoles(_config.MemberRoleName, true).FirstOrDefault();
            //        if (role == null) continue;

            //        foreach (var user in server.Users)
            //        {
            //            try
            //            {
            //                var dbuser = linkedusers.FirstOrDefault(x => (ulong)x.DiscordId == user.Id);
            //                if (dbuser == null)
            //                {
            //                    if (user.HasRole(role))
            //                    {
            //                        Logging.Log($@"Removing role ({role.Name}) from user ({user.Name}#{user.Discriminator})", GetType(), LogLevel.DEBUG);
            //                        await user.RemoveRoles(role);
            //                    }
            //                    continue;
            //                }

            //                if (!user.HasRole(role))
            //                {
            //                    Logging.Log($@"Adding role ({role.Name}) from user ({user.Name}#{user.Discriminator})", GetType(), LogLevel.DEBUG);
            //                    await user.AddRoles(role);
            //                }

            //                var player = _context.Players?.FirstOrDefault(x => { long steamId = 0; return long.TryParse(x.SteamId, out steamId) ? steamId == dbuser.SteamId : false; });
            //                var playerName = player?.Name?.Length > 32 ? player?.Name?.Substring(0, 32) : player?.Name;
            //                if (!string.IsNullOrWhiteSpace(playerName) 
            //                    && !user.ServerPermissions.Administrator
            //                    && !playerName.Equals(user.Name, StringComparison.Ordinal) 
            //                    && (user.Nickname == null || !playerName.Equals(user.Nickname, StringComparison.Ordinal)))
            //                {
            //                    //must be less or equal to 32 characters
            //                    Logging.Log($@"Changing nickname (from: ""{user.Nickname ?? "null"}"", to: ""{playerName}"") for user ({user.Name}#{user.Discriminator})", GetType(), LogLevel.DEBUG);
            //                    await user.Edit(nickname: playerName);
            //                }
            //            }
            //            catch (HttpException ex)
            //            {
            //                //could be due to the order of roles on the server. bot role with "manage roles"/"change nickname" permission must be higher up than the role it is trying to set
            //                Logging.LogException("HttpException while trying to update nicknames/roles (could be due to permissions)", ex, GetType(), LogLevel.DEBUG, ExceptionLevel.Ignored);
            //            }
            //        }
            //    }
            //}
            //catch(WebException ex)
            //{
            //    Logging.LogException("Exception while trying to update nicknames/roles", ex, GetType(), LogLevel.DEBUG, ExceptionLevel.Ignored);
            //}
        }

        private async void _openId_SteamOpenIdCallback(object sender, SteamOpenIdCallbackEventArgs e)
        {
            if (e.Successful)
            {
                var player = new
                {
                    RealName = (string)null,
                    PersonaName = (string)null
                };
                try
                {
                    using (var wc = new WebClient())
                    {
                        var data = await wc.DownloadStringTaskAsync($@"http://api.steampowered.com/ISteamUser/GetPlayerSummaries/v0002/?key={_config.SteamApiKey}&steamids={e.SteamId}");
                        var response = JsonConvert.DeserializeAnonymousType(data, new { response = new { players = new[] { player } } });
                        player = response?.response?.players?.FirstOrDefault();
                    }
                }
                catch { /* ignore exceptions */ }

                //QueryMaster.Steam.GetPlayerSummariesResponsePlayer player = null;
                //await Task.Factory.StartNew(() =>
                //{
                //    try
                //    {
                //        //this results in an exception (but it is easy enough to query by ourselves)
                //        var query = new QueryMaster.Steam.SteamQuery(_config.SteamApiKey);
                //        var result = query?.ISteamUser.GetPlayerSummaries(new[] { e.SteamId });
                //        if (result == null || !result.IsSuccess) return;

                //        player = result.ParsedResponse.Players.FirstOrDefault();
                //    }
                //    catch { /* ignore exceptions */}
                //});

                //set ark role on users when they link
                //foreach(var server in _discord.Servers)
                //{
                //    var user = server.GetUser(e.DiscordUserId);
                //    var role = server.FindRoles(_config.MemberRoleName, true).FirstOrDefault();
                //    if (user == null || role == null) continue;

                //    //try
                //    //{
                //    //    if (!user.HasRole(role)) await user.AddRoles(role);

                //    //    var p = _context.Players?.FirstOrDefault(x => { ulong steamId = 0; return ulong.TryParse(x.SteamId, out steamId) ? steamId == e.SteamId : false; });
                //    //    if (p != null && !string.IsNullOrWhiteSpace(p.Name))
                //    //    {

                //    //        //must be less or equal to 32 characters
                //    //        await user.Edit(nickname: p.Name.Length > 32 ? p.Name.Substring(0, 32) : p.Name);

                //    //    }
                //    //}
                //    //catch (HttpException)
                //    //{
                //    //    //could be due to the order of roles on the server. bot role with "manage roles"/"change nickname" permission must be higher up than the role it is trying to set
                //    //}
                //}

                using (var context = _databaseContextFactory.Create())
                {
                    var user = context.Users.FirstOrDefault(x => x.DiscordId == (long)e.DiscordUserId);
                    if (user != null)
                    {
                        user.RealName = player?.RealName;
                        user.SteamDisplayName = player?.PersonaName;
                        user.SteamId = (long)e.SteamId;
                        user.Unlinked = false;
                    }
                    else
                    {
                        user = new Database.Model.User { DiscordId = (long)e.DiscordUserId, SteamId = (long)e.SteamId, RealName = player?.RealName, SteamDisplayName = player?.PersonaName };
                        context.Users.Add(user);
                    }

                    foreach(var associatePlayed in context.Played.Where(x => x.SteamId == (long)e.SteamId))
                    {
                        associatePlayed.SteamId = null;
                        user.Played.Add(associatePlayed);
                    }

                    context.SaveChanges();
                }

                var ch = await _discord.GetUser(e.DiscordUserId).GetOrCreateDMChannelAsync();
                if (ch != null) await ch.SendMessageAsync($"Your Discord user is now linked with your Steam account! :)");
            }
            else
            {
                var ch = await _discord.GetUser(e.DiscordUserId).GetOrCreateDMChannelAsync();
                if (ch != null) await ch.SendMessageAsync($"Something went wrong during the linking process. Please try again later!");
            }
        }

        public async Task Initialize(CancellationToken token, bool skipExtract = false)
        {
            await _commands.AddModulesAsync(Assembly.GetEntryAssembly());

            //await _context.Initialize(token, skipExtract);
        }

        public async Task Start(ArkSpeciesAliases aliases = null)
        {
            await _discord.LoginAsync(TokenType.Bot, _config.BotToken);
            await _discord.StartAsync();

            //await _discord.Connect(_config.BotToken, TokenType.Bot);
        }

        public async Task Stop()
        {
            await _discord.StopAsync();
            //await _discord.Disconnect();
        }

        #region IDisposable Support
        private bool disposedValue = false; // To detect redundant calls

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    // TODO: dispose managed state (managed objects).
                    _discord?.Dispose();
                    _discord = null;

                    _openId.SteamOpenIdCallback -= _openId_SteamOpenIdCallback;
                }

                // TODO: free unmanaged resources (unmanaged objects) and override a finalizer below.
                // TODO: set large fields to null.

                disposedValue = true;
            }
        }

        // TODO: override a finalizer only if Dispose(bool disposing) above has code to free unmanaged resources.
        // ~ArkBot() {
        //   // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
        //   Dispose(false);
        // }

        // This code added to correctly implement the disposable pattern.
        public void Dispose()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            Dispose(true);
            // TODO: uncomment the following line if the finalizer is overridden above.
            // GC.SuppressFinalize(this);
        }
        #endregion
    }
}
