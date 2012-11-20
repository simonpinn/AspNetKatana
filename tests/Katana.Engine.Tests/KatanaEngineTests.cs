﻿// <copyright file="KatanaEngineTests.cs" company="Katana contributors">
//   Copyright 2011-2012 Katana contributors
// </copyright>
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
//     http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Katana.Engine.Settings;
using Katana.Engine.Utils;
using Shouldly;
using Xunit;

namespace Katana.Engine.Tests
{
    using AppFunc = Func<IDictionary<string, object>, Task>;

    public class KatanaEngineTests
    {
        public KatanaEngineTests()
        {
            Output = new StringWriter();
        }

        private TextWriter Output { get; set; }

        private IDictionary<string, object> CreateEmptyRequest()
        {
            return new Dictionary<string, object>();
        }

        private static Task GetCallCompletion(IDictionary<string, object> env)
        {
            return (Task)env["owin.CallCompleted"];
        }

        [Fact]
        public Task TextWriterAddedIfNotPresentInEnvironment()
        {
            object actualOutput = null;
            var encapsulateOutput = new StringWriter();

            var middleware = new Encapsulate(env =>
            {
                actualOutput = env["host.TraceOutput"];
                return TaskHelpers.Completed();
            }, encapsulateOutput);

            return middleware.Invoke(CreateEmptyRequest()).Then(() => { actualOutput.ShouldBeSameAs(encapsulateOutput); });
        }

        [Fact]
        public Task TextWriterNotChangedIfPresent()
        {
            object actualOutput = null;
            var encapsulateOutput = new StringWriter();
            var environmentOutput = new StringWriter();

            var middleware = new Encapsulate(env =>
            {
                actualOutput = env["host.TraceOutput"];
                return TaskHelpers.Completed();
            }, encapsulateOutput);

            IDictionary<string, object> env2 = CreateEmptyRequest();
            env2["host.TraceOutput"] = environmentOutput;

            return middleware.Invoke(env2).Then(() =>
            {
                actualOutput.ShouldBeSameAs(environmentOutput);
                actualOutput.ShouldNotBeSameAs(encapsulateOutput);
            });
        }

        [Fact]
        public Task CallCompletedNotChangedIfPresent()
        {
            bool callCompleted = false;

            var middleware = new Encapsulate(
                env =>
                {
                    GetCallCompletion(env).Finally(() => callCompleted = true, true);
                    return TaskHelpers.Completed();
                }, Output);

            var tcs = new TaskCompletionSource<object>();
            IDictionary<string, object> env2 = CreateEmptyRequest();
            env2["owin.CallCompleted"] = tcs.Task;

            return middleware.Invoke(env2).Then(() =>
            {
                callCompleted.ShouldBe(false);
                tcs.TrySetResult(null);
                callCompleted.ShouldBe(true);
            });
        }

        [Fact]
        public Task CallCompletedProvidedIfMissing()
        {
            Task callCompleted = null;

            var middleware = new Encapsulate(env =>
            {
                callCompleted = GetCallCompletion(env);
                return TaskHelpers.Completed();
            }, Output);

            return middleware.Invoke(CreateEmptyRequest())
                .Then(() => callCompleted.ShouldNotBe(null));
        }

        [Fact]
        public Task AsyncFaultWillTriggerTheProvidedToken()
        {
            bool callCompleted = false;
            var tcs = new TaskCompletionSource<object>();

            var middleware = new Encapsulate(env =>
            {
                GetCallCompletion(env).Finally(() => callCompleted = true, true);
                return tcs.Task;
            }, Output);

            Task task = middleware.Invoke(CreateEmptyRequest())
                .Catch(info => info.Handled())
                .Then(() => callCompleted.ShouldBe(true));

            callCompleted.ShouldBe(false); // disposed before exception
            task.IsCompleted.ShouldBe(false); // Completed before exception.

            tcs.TrySetException(new Exception("Simulating Async Exception"));

            return task;
        }

        [Fact]
        public void SyncFaultWillTriggerTheProvidedToken()
        {
            bool callCompleted = false;

            var middleware = new Encapsulate(env =>
            {
                GetCallCompletion(env).Finally(() => callCompleted = true, true);
                throw new ApplicationException("Boom");
            }, Output);

            Exception caught = null;
            try
            {
                middleware.Invoke(CreateEmptyRequest());
            }
            catch (Exception ex)
            {
                caught = ex;
            }
            callCompleted.ShouldBe(true);
            caught.ShouldNotBe(null);
            caught.Message.ShouldBe("Boom");
        }

        [Fact]
        public void InitializeAndCreateShouldBeCalledWithProperties()
        {
            var serverFactoryAlpha = new ServerFactoryAlpha();
            var startInfo = new StartContext
            {
                ServerFactory = serverFactoryAlpha,
                App = new AppFunc(env => TaskHelpers.Completed()),
            };
            var settings = new KatanaSettings();
            var engine = new KatanaEngine(settings);
            serverFactoryAlpha.InitializeCalled.ShouldBe(false);
            serverFactoryAlpha.CreateCalled.ShouldBe(false);
            IDisposable server = engine.Start(startInfo);

            serverFactoryAlpha.InitializeCalled.ShouldBe(true);
            serverFactoryAlpha.CreateCalled.ShouldBe(true);
            serverFactoryAlpha.InitializeProperties.ShouldBeSameAs(serverFactoryAlpha.CreateProperties);
            server.Dispose();
        }

        public class ServerFactoryAlpha
        {
            public bool InitializeCalled { get; set; }
            public IDictionary<string, object> InitializeProperties { get; set; }
            public bool CreateCalled { get; set; }
            public IDictionary<string, object> CreateProperties { get; set; }

            public void Initialize(IDictionary<string, object> properties)
            {
                InitializeCalled = true;
                InitializeProperties = properties;
            }

            public IDisposable Create(AppFunc app, IDictionary<string, object> properties)
            {
                CreateCalled = true;
                CreateProperties = properties;
                return new Disposable(() => { });
            }
        }

        [Fact]
        public void CreateShouldBeProvidedWithAdaptedAppIfNeeded()
        {
            var serverFactoryBeta = new ServerFactoryBeta();
            var startInfo = new StartContext
            {
                ServerFactory = serverFactoryBeta,
                App = new AppFunc(env => TaskHelpers.Completed()),
            };
            var settings = new KatanaSettings();
            var engine = new KatanaEngine(settings);
            serverFactoryBeta.CreateCalled.ShouldBe(false);
            IDisposable server = engine.Start(startInfo);
            serverFactoryBeta.CreateCalled.ShouldBe(true);
            server.Dispose();
        }

        public class ServerFactoryBeta
        {
            public bool CreateCalled { get; set; }

            public IDisposable Create(AppFunc app, IDictionary<string, object> properties)
            {
                CreateCalled = true;
                return new Disposable(() => { });
            }
        }

        [Fact]
        public void PropertiesShouldHaveExpectedKeysFromHost()
        {
            var serverFactory = new ServerFactoryAlpha();
            var startInfo = new StartContext
            {
                ServerFactory = serverFactory,
                App = new AppFunc(env => TaskHelpers.Completed()),
            };
            var settings = new KatanaSettings();
            var engine = new KatanaEngine(settings);
            serverFactory.InitializeCalled.ShouldBe(false);
            serverFactory.CreateCalled.ShouldBe(false);
            IDisposable server = engine.Start(startInfo);

            serverFactory.InitializeProperties.ShouldContainKey("host.TraceOutput");
            serverFactory.InitializeProperties.ShouldContainKey("host.Addresses");

            serverFactory.InitializeProperties["host.TraceOutput"].ShouldBeTypeOf<TextWriter>();
            serverFactory.InitializeProperties["host.Addresses"].ShouldBeTypeOf<IList<IDictionary<string, object>>>();

            server.Dispose();
        }
    }
}