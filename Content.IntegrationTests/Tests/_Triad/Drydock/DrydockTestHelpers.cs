#nullable enable

using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Content.IntegrationTests.Pair;
using Content.Server.Database;
using Robust.Shared.Log;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// Shared test infrastructure for drydock integration tests.
    /// </summary>
    internal static class DrydockTestHelpers
    {
        /// <summary>
        /// The owner column is a real foreign key, so a ship cannot be filed for a player who does
        /// not exist. That is the intended behaviour, and it means a test has to supply one.
        /// </summary>
        internal static Task InsertPlayer(IServerDbManager db, Guid userId)
        {
            return db.RunTriadDbCommand(async (context, token) =>
            {
                context.Player.Add(new Player
                {
                    UserId = userId,
                    LastSeenUserName = $"drydock-test-{userId:N}",
                    FirstSeenTime = DateTime.UtcNow,
                    LastSeenTime = DateTime.UtcNow,
                    LastSeenAddress = IPAddress.Loopback,
                });

                await context.SaveChangesAsync(token);
            }, CancellationToken.None);
        }

        /// <summary>
        /// Starts a server-side async operation on the game thread and pumps the pair until it
        /// finishes. Both pipelines await database work, so the continuation has to come back to a
        /// ticking server; awaiting the task from the test thread alone would never let it resume.
        ///
        /// <para>Bounded by the wall clock, not by a tick count. <see cref="DrydockRoundTripTest.BuildShipAndStation"/>
        /// sets <c>triad.drydock.tick_budget_ms</c> to zero, so no job is made and the only real
        /// suspensions left are the store's three thread-pool hops, which are real time on another
        /// thread rather than ticks here: a fixed tick ceiling drains in well under a second on an
        /// idle pair and then calls a store that is merely parked "never completed". Anything that
        /// deliberately exercises slicing pumps its own loop rather than borrowing this one.</para>
        /// </summary>
        /// <param name="timeout">The wall-clock deadline. Sixty seconds when not given.</param>
        internal static async Task<T> RunOnServer<T>(TestPair pair, Func<Task<T>> start, TimeSpan? timeout = null)
        {
            var limit = timeout ?? TimeSpan.FromSeconds(60);

            Task<T>? task = null;
            await pair.Server.WaitPost(() => task = start());

            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (!task!.IsCompleted && deadline.Elapsed < limit)
            {
                await pair.RunTicksSync(1);
            }

            Assert.That(task!.IsCompleted, Is.True,
                "The drydock operation never completed: either it is blocked on the database, or a continuation never came back to the game thread.");

            return await task;
        }

        /// <summary>Runs an operation that is meant to log errors, without the pair failing on them.</summary>
        internal static async Task<T> Quietly<T>(TestPair pair, Func<Task<T>> run)
        {
            var failureLevel = pair.ServerLogHandler.FailureLevel;
            pair.ServerLogHandler.FailureLevel = LogLevel.Fatal;
            try
            {
                return await run();
            }
            finally
            {
                pair.ServerLogHandler.FailureLevel = failureLevel;
            }
        }
    }
}
