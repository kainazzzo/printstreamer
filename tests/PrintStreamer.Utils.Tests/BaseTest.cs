using System;
using Autofac.Extras.Moq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PrintStreamer.Utils.Tests
{
    [TestClass]
    public abstract class BaseTest<TSut> where TSut : class
    {
        protected AutoMock AutoMock { get; set; } = null!;
        protected TSut Sut { get; set; } = null!;

        [ClassInitialize]
        public static void ClassInitialize(TestContext context)
        {
            // Class-level initialization if needed
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            // Class-level cleanup if needed
        }

        [TestInitialize]
        public virtual void TestInitialize()
        {
            AutoMock = Autofac.Extras.Moq.AutoMock.GetLoose();
        }

        [TestCleanup]
        public void TestCleanup()
        {
            AutoMock?.Dispose();
        }
    }
}