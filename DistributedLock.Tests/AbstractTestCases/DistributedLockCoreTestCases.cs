﻿using Medallion.Shell;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Medallion.Threading.Tests
{
    public abstract class DistributedLockCoreTestCases<TEngine>
        where TEngine : TestingDistributedLockEngine, new()
    {
        [Test]
        public void BasicTest()
        {
            using var engine = new TEngine();
            var @lock = engine.CreateLock(nameof(BasicTest));
            var lock2 = engine.CreateLock(nameof(BasicTest) + "2");

            using (var handle = @lock.TryAcquire())
            {
                Assert.IsNotNull(handle, this.GetType() + ": should be able to acquire new lock");

                using (var nestedHandle = @lock.TryAcquire())
                {
                    (nestedHandle == null).ShouldEqual(!engine.IsReentrant, this.GetType() + ": reentrancy mis-stated");
                }

                using var nestedHandle2 = lock2.TryAcquire();
                Assert.IsNotNull(nestedHandle2, this.GetType() + ": should be able to acquire a different lock");
            }

            using (var handle = @lock.TryAcquire())
            {
                Assert.IsNotNull(handle, this.GetType() + ": should be able to re-acquire after releasing");
            }
        }

        [Test]
        public void BasicAsyncTest()
        {
            using var engine = new TEngine();
            var @lock = engine.CreateLock(nameof(BasicAsyncTest));
            var lock2 = engine.CreateLock(nameof(BasicAsyncTest) + "2");

            using (var handle = @lock.TryAcquireAsync().Result)
            {
                Assert.IsNotNull(handle, this.GetType().Name);

                using (var nestedHandle = @lock.TryAcquireAsync().Result)
                {
                    (nestedHandle == null).ShouldEqual(!engine.IsReentrant, this.GetType().Name);
                }

                using var nestedHandle2 = lock2.TryAcquireAsync().Result;
                Assert.IsNotNull(nestedHandle2, this.GetType().Name);
            }

            using (var handle = @lock.TryAcquireAsync().Result)
            {
                Assert.IsNotNull(handle, this.GetType().Name);
            }
        }

        [Test]
        public void TestBadArguments()
        {
            using var engine = new TEngine();
            var @lock = engine.CreateLock(nameof(TestBadArguments));
            Assert.Catch<ArgumentOutOfRangeException>(() => @lock.Acquire(TimeSpan.FromSeconds(-2)));
            Assert.Catch<ArgumentOutOfRangeException>(() => @lock.AcquireAsync(TimeSpan.FromSeconds(-2)));
            Assert.Catch<ArgumentOutOfRangeException>(() => @lock.TryAcquire(TimeSpan.FromSeconds(-2)));
            Assert.Catch<ArgumentOutOfRangeException>(() => @lock.TryAcquireAsync(TimeSpan.FromSeconds(-2)));
            Assert.Catch<ArgumentOutOfRangeException>(() => @lock.Acquire(TimeSpan.FromSeconds(int.MaxValue)));
            Assert.Catch<ArgumentOutOfRangeException>(() => @lock.AcquireAsync(TimeSpan.FromSeconds(int.MaxValue)));
            Assert.Catch<ArgumentOutOfRangeException>(() => @lock.TryAcquire(TimeSpan.FromSeconds(int.MaxValue)));
            Assert.Catch<ArgumentOutOfRangeException>(() => @lock.TryAcquireAsync(TimeSpan.FromSeconds(int.MaxValue)));
        }

        [Test]
        public void TestDisposeHandleIsIdempotent()
        {
            using var engine = new TEngine();
            var @lock = engine.CreateLock(nameof(TestDisposeHandleIsIdempotent));
            var handle = @lock.Acquire(TimeSpan.FromSeconds(30));
            Assert.IsNotNull(handle);
            handle.Dispose();
            var handle2 = @lock.Acquire(TimeSpan.FromSeconds(30));
            Assert.DoesNotThrow(() => handle.Dispose());
            Assert.DoesNotThrow(() => handle2.Dispose());
        }

        [Test]
        [NonParallelizable] // timing-sensitive
        public void TestTimeouts()
        {
            using var engine = new TEngine();
            var @lock = engine.CreateLock(nameof(TestTimeouts));
            // acquire with a different lock instance to avoid reentrancy mattering
            using (engine.CreateLock(nameof(TestTimeouts)).Acquire())
            {
                var timeout = TimeSpan.FromSeconds(.1);
                var waitTime = TimeSpan.FromSeconds(.4);

                var syncAcquireTask = Task.Run(() => @lock.Acquire(timeout));
                syncAcquireTask.ContinueWith(_ => { }).Wait(waitTime).ShouldEqual(true, "sync acquire");
                Assert.IsInstanceOf<TimeoutException>(syncAcquireTask.Exception!.InnerException, "sync acquire");

                var asyncAcquireTask = @lock.AcquireAsync(timeout).AsTask();
                asyncAcquireTask.ContinueWith(_ => { }).Wait(waitTime).ShouldEqual(true, "async acquire");
                Assert.IsInstanceOf<TimeoutException>(asyncAcquireTask.Exception!.InnerException, "async acquire");

                var syncTryAcquireTask = Task.Run(() => @lock.TryAcquire(timeout));
                syncTryAcquireTask.Wait(waitTime).ShouldEqual(true, "sync tryAcquire");
                syncTryAcquireTask.Result.ShouldEqual(null, "sync tryAcquire");

                var asyncTryAcquireTask = @lock.TryAcquireAsync(timeout).AsTask();
                asyncTryAcquireTask.Wait(waitTime).ShouldEqual(true, "async tryAcquire");
                asyncTryAcquireTask.Result.ShouldEqual(null, "async tryAcquire");
            }
        }

        [Test]
        public void CancellationTest()
        {
            using var engine = new TEngine();
            var lockName = nameof(CancellationTest);
            var @lock = engine.CreateLock(lockName);

            var source = new CancellationTokenSource();
            using (var handle = engine.CreateLock(lockName).Acquire())
            {
                var blocked = @lock.AcquireAsync(cancellationToken: source.Token).AsTask();
                blocked.Wait(TimeSpan.FromSeconds(.1)).ShouldEqual(false);
                source.Cancel();
                blocked.ContinueWith(_ => { }).Wait(TimeSpan.FromSeconds(10)).ShouldEqual(true, this.GetType().Name);
                blocked.Status.ShouldEqual(TaskStatus.Canceled, (blocked.Exception ?? (object)"no exception").ToString());
            }

            // already canceled
            source = new CancellationTokenSource();
            source.Cancel();
            Assert.Catch<OperationCanceledException>(() => @lock.Acquire(cancellationToken: source.Token));
        }

        [Test]
        public void TestParallelism()
        {
            // NOTE: if this test fails for Postgres, we may need to raise the default connection limit. This can 
            // be done by setting max_connections in C:\Program Files\PostgreSQL\<version>\data\postgresql.conf or 
            // /var/lib/pgsql/<version>/data/postgresql.conf and then restarting Postgres.
            // See https://docs.alfresco.com/5.0/tasks/postgresql-config.html

            using var engine = new TEngine();
            var counter = 0;
            var tasks = Enumerable.Range(1, 100).Select(async _ =>
                {
                    var @lock = engine.CreateLock("parallel_test");
                    using (await @lock.AcquireAsync())
                    {
                            // increment going in
                            Interlocked.Increment(ref counter);

                            // hang out for a bit to ensure concurrency
                            await Task.Delay(TimeSpan.FromMilliseconds(10));

                            // decrement and return on the way out (returns # inside the lock when this left ... should be 0)
                            return Interlocked.Decrement(ref counter);
                    }
                })
                .ToList();

            Task.WaitAll(tasks.ToArray<Task>(), TimeSpan.FromSeconds(30)).ShouldEqual(true, this.GetType().Name);

            tasks.ForEach(t => t.Result.ShouldEqual(0));
        }

        [Test]
        public void TestGetSafeLockName()
        {
            using var engine = new TEngine();
            Assert.Catch<ArgumentNullException>(() => engine.GetSafeName(null!));

            foreach (var name in new[] { string.Empty, new string('a', 1000), @"\\\\\", new string('\\', 1000) })
            {
                var safeName = engine.GetSafeName(name);
                Assert.DoesNotThrow(() => engine.CreateLockWithExactName(safeName).Acquire(TimeSpan.FromSeconds(10)).Dispose(), $"{this.GetType().Name}: could not acquire '{name}'");
            }
        }

        [Test]
        public void TestGetSafeLockNameIsCaseSensitive()
        {
            var longName1 = new string('a', 1000);
            var longName2 = new string('a', longName1.Length - 1) + "A";
            StringComparer.OrdinalIgnoreCase.Equals(longName1, longName2).ShouldEqual(true, "sanity check");

            using var engine = new TEngine();
            Assert.AreNotEqual(engine.GetSafeName(longName1), engine.GetSafeName(longName2));
        }

        [Test]
        public async Task TestLockNamesAreCaseSensitive()
        {
            using var engine = new TEngine();

            // the goal here is to construct 2 valid lock names that differ only by case. We start by generating a hash name
            // that is unique to this test yet stable across runs. Then we truncate it to avoid need for further hashing in Postgres
            // (which only supports very short ASCII string names). Finally, we re-run through GetSafeName to pick up any special prefix
            // that is needed (e. g. for wait handles)
            using var sha1 = SHA1.Create();
            var uniqueHashName = Convert.ToBase64String(sha1.ComputeHash(Encoding.UTF8.GetBytes(engine.GetUniqueSafeLockName())));
            var lowerName = engine.GetSafeName($"{uniqueHashName.Substring(0, 6)}_a");
            var upperName = engine.GetSafeName($"{uniqueHashName.Substring(0, 6)}_A");
            // make sure we succeeded in generating what we set out ot generate
            Assert.AreNotEqual(lowerName, upperName);
            Assert.IsTrue(StringComparer.OrdinalIgnoreCase.Equals(lowerName, upperName));

            await using (await engine.CreateLockWithExactName(lowerName).AcquireAsync())
            await using (var handle = await engine.CreateLockWithExactName(upperName).TryAcquireAsync())
            {
                Assert.IsNotNull(handle);
            }
        }

        [Test]
        public void TestCanceledAlreadyThrowsForSyncAndDoesNotThrowForAsync()
        {
            using var engine = new TEngine();
            using var source = new CancellationTokenSource();
            source.Cancel();

            var @lock = engine.CreateLock("already-canceled");

            Assert.Catch<OperationCanceledException>(() => @lock.Acquire(cancellationToken: source.Token));
            Assert.Catch<OperationCanceledException>(() => @lock.TryAcquire(cancellationToken: source.Token));

            var acquireTask = @lock.AcquireAsync(cancellationToken: source.Token).AsTask();
            acquireTask.ContinueWith(_ => { }).Wait(TimeSpan.FromSeconds(10)).ShouldEqual(true);
            acquireTask.IsCanceled.ShouldEqual(true, "acquire");

            var tryAcquireTask = @lock.TryAcquireAsync(cancellationToken: source.Token).AsTask();
            tryAcquireTask.ContinueWith(_ => { }).Wait(TimeSpan.FromSeconds(10)).ShouldEqual(true);
            tryAcquireTask.IsCanceled.ShouldEqual(true, "tryAcquire");
        }

        [Test]
        public void TestLockAbandonment()
        {
            using var engine = new TEngine();
            var lockName = nameof(TestLockAbandonment);
            new Action<string>(name => engine.CreateLock(name).Acquire())(lockName);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            engine.PerformCleanupForLockAbandonment();

            using var handle = engine.CreateLock(lockName).TryAcquire();
            Assert.IsNotNull(handle, this.GetType().Name);
        }

        [Test]
        public void TestCrossProcess()
        {
            using var engine = new TEngine();
            var lockName = engine.GetUniqueSafeLockName();
            var command = RunLockTaker(engine, engine.CrossProcessLockType, lockName);
            Assert.IsTrue(command.StandardOutput.ReadLineAsync().Wait(TimeSpan.FromSeconds(10)));
            Assert.IsFalse(command.Task.Wait(TimeSpan.FromSeconds(.1)));

            var @lock = engine.CreateLockWithExactName(lockName);
            @lock.TryAcquire().ShouldEqual(null, this.GetType().Name);

            command.StandardInput.WriteLine("done");
            command.StandardInput.Flush();
            
            using var handle = @lock.TryAcquire(TimeSpan.FromSeconds(10));
            Assert.IsNotNull(handle, this.GetType().Name);

            Assert.IsTrue(command.Task.Wait(TimeSpan.FromSeconds(10)));
        }

        [Test]
        public void TestCrossProcessAbandonment()
        {
            this.CrossProcessAbandonmentHelper(asyncWait: false, kill: false);
        }

        [Test]
        public void TestCrossProcessAbandonmentAsync()
        {
            this.CrossProcessAbandonmentHelper(asyncWait: true, kill: false);
        }

        [Test]
        public void TestCrossProcessAbandonmentWithKill()
        {
            this.CrossProcessAbandonmentHelper(asyncWait: false, kill: true);
        }

        [Test]
        public void TestCrossProcessAbandonmentWithKillAsync()
        {
            this.CrossProcessAbandonmentHelper(asyncWait: true, kill: true);
        }

        private void CrossProcessAbandonmentHelper(bool asyncWait, bool kill)
        {
            using var engine = new TEngine();
            var name = engine.GetUniqueSafeLockName($"cpl-{asyncWait}-{kill}");
            var command = RunLockTaker(engine, engine.CrossProcessLockType, name);
            Assert.IsTrue(command.StandardOutput.ReadLineAsync().Wait(TimeSpan.FromSeconds(10)));
            Assert.IsFalse(command.Task.IsCompleted);

            var @lock = engine.CreateLockWithExactName(name);

            var acquireTask = asyncWait
                ? @lock.TryAcquireAsync(TimeSpan.FromSeconds(10)).AsTask()
                : Task.Run(() => @lock.TryAcquire(TimeSpan.FromSeconds(10)));
            acquireTask.Wait(TimeSpan.FromSeconds(.1)).ShouldEqual(false, this.GetType().Name);

            if (kill)
            {
                command.Kill();
            }
            else
            {
                command.StandardInput.WriteLine("abandon");
                command.StandardInput.Flush();
            }

            using var handle = acquireTask.Result;
            Assert.IsNotNull(handle, this.GetType().Name);
        }

        private static Command RunLockTaker(TEngine engine, params string[] args)
        {
            const string Configuration =
#if DEBUG
                "Debug";
#else
                "Release";
#endif
            var exePath = Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "DistributedLockTaker", "bin", Configuration, TestHelper.FrameworkName, "DistributedLockTaker.exe");
            var command = Command.Run(exePath, args, o => o.WorkingDirectory(TestContext.CurrentContext.TestDirectory).ThrowOnError(true))
                .RedirectStandardErrorTo(Console.Error);
            engine.RegisterCleanupAction(() =>
            {
                if (!command.Task.IsCompleted)
                {
                    command.Kill();
                }
            });
            return command;
        }
    }
}
