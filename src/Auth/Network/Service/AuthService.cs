﻿using System;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Threading.Tasks;
using BlubLib.Network;
using BlubLib.Network.Pipes;
using BlubLib.Network.Transport.Sockets;
using BlubLib.Security.Cryptography;
using Netsphere.Network.Message.Auth;
using NLog;
using Shaolinq;

namespace Netsphere.Network.Service
{
    internal class AuthService : MessageHandler
    {
        // ReSharper disable once InconsistentNaming
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        [MessageHandler(typeof(CAuthInEUReqMessage))]
        public async Task LoginHandler(ISession session, CAuthInEUReqMessage message)
        {
            var ip = ((IPEndPoint)((TcpTransport)session.Transport).Socket.RemoteEndPoint).Address.ToString();
            Logger.Debug($"Login from {ip} with username {message.Username}");

            var db = AuthDatabase.Instance;
            var account = await db.Accounts.FirstOrDefaultAsync(acc => acc.Username == message.Username)
                .ConfigureAwait(false);

            if (account == null)
            {
                if (Config.Instance.NoobMode)
                {
                    // NoobMode: Create a new account if non exists
                    using (var scope = new DataAccessScope())
                    {
                        account = db.Accounts.Create();
                        account.Username = message.Username;

                        var bytes = new byte[16];
                        using (var rng = new RNGCryptoServiceProvider())
                            rng.GetBytes(bytes);

                        account.Salt = Hash.GetString<SHA1CryptoServiceProvider>(bytes);
                        account.Password = Hash.GetString<SHA1CryptoServiceProvider>(message.Password + "+" + account.Salt);

                        await scope.CompleteAsync()
                            .ConfigureAwait(false);
                    }
                }
                else
                {
                    Logger.Error($"Wrong login for {message.Username}");
                    await session.SendAsync(new SAuthInEuAckMessage(AuthLoginResult.WrongIdorPw))
                        .ConfigureAwait(false);
                    return;
                }
            }

            var password = Hash.GetString<SHA1CryptoServiceProvider>(message.Password + "+" + account.Salt);
            if (!account.Password.Equals(password, StringComparison.InvariantCultureIgnoreCase))
            {
                if (Config.Instance.NoobMode)
                {
                    // Noob Mode: Save new password
                    using (var scope = new DataAccessScope())
                    {
                        var acc = db.Accounts.GetReference(account.Id);
                        var bytes = new byte[16];
                        using (var rng = new RNGCryptoServiceProvider())
                            rng.GetBytes(bytes);

                        var salt = Hash.GetString<SHA1CryptoServiceProvider>(bytes);
                        password = Hash.GetString<SHA1CryptoServiceProvider>(message.Password + "+" + salt);
                        acc.Password = password;
                        acc.Salt = salt;

                        await scope.CompleteAsync()
                            .ConfigureAwait(false);
                    }
                }
                else
                {
                    Logger.Error($"Wrong login for {message.Username}");
                    await session.SendAsync(new SAuthInEuAckMessage(AuthLoginResult.WrongIdorPw))
                        .ConfigureAwait(false);
                    return;
                }
            }

            var now = DateTimeOffset.Now.ToUnixTimeSeconds();
            var ban = await account.Bans.FirstOrDefaultAsync(b => b.Date + b.Duration > now)
                .ConfigureAwait(false);
            if (ban != null)
            {
                var unbanDate = DateTimeOffset.FromUnixTimeSeconds(ban.Date + ban.Duration);
                Logger.Error($"{message.Username} is banned until {unbanDate}");
                await session.SendAsync(new SAuthInEuAckMessage(unbanDate))
                    .ConfigureAwait(false);
                return;
            }

            Logger.Info($"Login success for {message.Username}");

            using (var scope = new DataAccessScope())
            {
                var entry = account.LoginHistory.Create();
                entry.Date = DateTimeOffset.Now.ToUnixTimeSeconds();
                entry.IP = ip;

                await scope.CompleteAsync()
                    .ConfigureAwait(false);
            }

            // ToDo proper session generation
            var sessionId = Hash.GetUInt32<CRC32>($"<{account.Username}+{password}>");
            await session.SendAsync(new SAuthInEuAckMessage(AuthLoginResult.OK, (ulong)account.Id, sessionId))
                    .ConfigureAwait(false);
        }

        [MessageHandler(typeof(CServerListReqMessage))]
        public Task ServerListHandler(AuthServer server, ISession session)
        {
            return session.SendAsync(new SServerListAckMessage(server.ServerManager.ToArray()));
        }
    }
}
