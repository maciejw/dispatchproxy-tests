using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using Xunit;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Test1
{
    interface IService1
    {
        void MyMethod1();
        string MyMethod2();
        Task MyMethod3();
        Task<string> MyMethod4();
    }
    class Service1 : IService1
    {
        private readonly ILogger<Service1> logger;

        public Service1(ILogger<Service1> logger)
        {
            this.logger = logger;
        }
        public void MyMethod1()
        {
            logger.LogInformation("hello");
        }
        public string MyMethod2()
        {
            logger.LogInformation("hello");
            return "sync result";
        }
        public Task MyMethod3()
        {
            return Task.CompletedTask;
        }

        public Task<string> MyMethod4()
        {
            return Task.FromResult("async result");
        }
    }

    public class LoggingDecorator<T> : DispatchProxy
    {
        public delegate void LoggingAction(ILogger logger, MethodInfo targetMethod, object[] args);
        public delegate void LoggingErrorAction(ILogger logger, Exception exception, MethodInfo targetMethod, object[] args);

        private Parameters parameters;

        public class Parameters
        {
            private LoggingAction defaultBeforeAction = (logger, targetMethod, args) => logger.LogInformation("Before '{name}'", targetMethod.Name);
            private LoggingAction defaultAfterAction = (logger, targetMethod, args) => logger.LogInformation("After '{name}'", targetMethod.Name);
            private LoggingErrorAction defaultErrorAction = (logger, exception, targetMethod, args) => logger.LogError(exception, "Error in '{name}'", targetMethod.Name);
            public Parameters(T decoratedService, ILogger logger, LoggingAction beforeAction = null, LoggingAction afterAction = null, LoggingErrorAction errorAction = null)
            {
                DecoratedService = decoratedService;
                Logger = logger;
                BeforeAction = beforeAction ?? defaultBeforeAction;
                AfterAction = afterAction ?? defaultAfterAction;
                ErrorAction = errorAction ?? defaultErrorAction;
            }

            public T DecoratedService { get; }
            public LoggingAction BeforeAction { get; }
            public LoggingAction AfterAction { get; }
            public LoggingErrorAction ErrorAction { get; }
            public ILogger Logger { get; }
        }


        public void Initialize(Parameters parameters)
        {
            this.parameters = parameters;
        }

        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            bool syncResult = true;
            try
            {
                parameters.BeforeAction(parameters.Logger, targetMethod, args);

                parameters.Logger.LogTrace("Method '{targetMethodName}' parameters '{parameters}'", targetMethod.Name, args);

                var result = targetMethod.Invoke(parameters.DecoratedService, args);

                if (result is Task task)
                {
                    syncResult = false;

                    task.ContinueWith((completedTask) =>
                    {
                        try
                        {
                            if (completedTask.IsCompletedSuccessfully)
                            {
                                Type type = task.GetType();
                                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
                                {
                                    if (parameters.Logger.IsEnabled(LogLevel.Trace))
                                    {
                                        var result = type.GetProperty("Result").GetValue(task);
                                        parameters.Logger.LogTrace("Task completed successfully from '{targetMethodName}' with '{result}'", targetMethod.Name, result);
                                    }
                                }
                            }
                            else
                            {
                                parameters.Logger.LogTrace("Task completed with '{status}'", task.Status);
                                if (completedTask.Exception != null)
                                {
                                    parameters.ErrorAction(parameters.Logger, completedTask.Exception, targetMethod, args);
                                }

                            }
                            return result;
                        }
                        finally
                        {
                            parameters.Logger.LogTrace("Completed '{targetMethodName}' in '{timeElapsedInTicks}'", targetMethod.Name, stopwatch.Elapsed.Ticks);
                            parameters.AfterAction(parameters.Logger, targetMethod, args);

                        }
                    }, scheduler: TaskScheduler.FromCurrentSynchronizationContext());
                }

                if (syncResult)
                {
                    if (targetMethod.ReturnType == typeof(void))
                    {
                        parameters.Logger.LogTrace("Completed '{targetMethodName}' in '{timeElapsedInTicks}'", targetMethod.Name, stopwatch.Elapsed.Ticks);
                    }
                    else
                    {
                        parameters.Logger.LogTrace("Completed '{targetMethodName}' with '{result}' in '{timeElapsedInTicks}'", targetMethod.Name, result, stopwatch.Elapsed.Ticks);
                    }
                }
                return result;
            }
            catch (Exception ex)
            {
                parameters.ErrorAction(parameters.Logger, ex, targetMethod, args);
                throw;
            }
            finally
            {
                if (syncResult)
                {
                    parameters.AfterAction(parameters.Logger, targetMethod, args);
                }
            }
        }
    }

    public class UnitTest1
    {
        public UnitTest1()
        {
            Log.Logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Seq("http://localhost:5341", LogEventLevel.Verbose)
            .MinimumLevel.Verbose()
            .CreateLogger();

        }

        [Fact]
        public async Task Test1()
        {
            ServiceCollection services = new ServiceCollection();


            services.AddLogging(logging => logging.AddSerilog());

            services.AddTransient<IService1, Service1>();
            services.Decorate<IService1>((decoratedService, serviceProvider) =>
            {
                var proxy = DispatchProxy.Create<IService1, LoggingDecorator<IService1>>();

                if (proxy is LoggingDecorator<IService1> decorator)
                {
                    var logger = serviceProvider.GetService<ILogger<LoggingDecorator<IService1>>>();

                    decorator.Initialize(new LoggingDecorator<IService1>.Parameters(decoratedService, logger));
                }
                
                return proxy;
            });

            using var serviceProvider = services.BuildServiceProvider();

            var service1 = serviceProvider.GetService<IService1>();
            service1 = serviceProvider.GetService<IService1>();

            Assert.NotNull(service1);

            service1.MyMethod1();
            service1.MyMethod1();

            Assert.Equal("sync result", service1.MyMethod2());
            Assert.Equal("sync result", service1.MyMethod2());

            await service1.MyMethod3();
            await service1.MyMethod3();

            Assert.Equal("async result", await service1.MyMethod4());
            Assert.Equal("async result", await service1.MyMethod4());



        }
    }
}
