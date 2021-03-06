﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.
using System.IO;
using Microsoft.Azure.WebJobs.Logging;
using Microsoft.Azure.WebJobs.Script.WebHost.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Microsoft.Azure.WebJobs.Script.Tests
{
    public class SystemLoggerProviderTests
    {
        private readonly IOptions<ScriptJobHostOptions> _options;
        private readonly IEnvironment _environment = new TestEnvironment();

        public SystemLoggerProviderTests()
        {
            var scriptOptions = new ScriptJobHostOptions
            {
                RootLogPath = Path.GetTempPath()
            };

            _options = new OptionsWrapper<ScriptJobHostOptions>(scriptOptions);
        }

        [Fact]
        public void CreateLogger_ReturnsSystemLogger_ForNonUserCategories()
        {
            var provider = new SystemLoggerProvider(_options, null, _environment);

            Assert.IsType<SystemLogger>(provider.CreateLogger(LogCategories.CreateFunctionCategory("TestFunction")));
            Assert.IsType<SystemLogger>(provider.CreateLogger(ScriptConstants.LogCategoryHostGeneral));
            Assert.IsType<SystemLogger>(provider.CreateLogger("NotAFunction.TestFunction.User"));
        }

        [Fact]
        public void CreateLogger_ReturnsNullLogger_ForUserCategory()
        {
            var provider = new SystemLoggerProvider(_options, null, _environment);

            Assert.IsType<NullLogger>(provider.CreateLogger(LogCategories.CreateFunctionUserCategory("TestFunction")));
        }
    }
}