using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace MassTransitBlazorSyncContext
{
    public class Program
    {
        private static IClientFactory _clientFactory;
        static async Task Main(string[] args)
        {
            var services = new ServiceCollection();
            services.AddMassTransit(configurator =>
            {
                configurator.AddBus(sp => Bus.Factory.CreateUsingRabbitMq(c =>
                {
                    c.ReceiveEndpoint(typeof(Program).FullName, e =>
                    {
                        e.Consumer<ExampleRequestHandler>(sp);
                    });
                }));

                configurator.AddConsumer<ExampleRequestHandler>();
                configurator.AddRequestClient<ExampleRequest>();
            });

            var sp = services.BuildServiceProvider();
            var busControl = sp.GetRequiredService<IBusControl>();
            await busControl.StartAsync();

            // start program logic
            _clientFactory = sp.GetService<IClientFactory>();

            var ctx = new RendererSynchronizationContext();
            await ctx.InvokeAsync(async () =>
            {
                Console.WriteLine("Sending request within synchronization context");

                // this works:
                //await Task.Run(SendRequest);

                // this does not work:
                await SendRequest().ConfigureAwait(false);
            });
        }

        private async static Task SendRequest()
        {
            var client = _clientFactory.CreateRequestClient<ExampleRequest>();

            try
            {
                var response = await client.GetResponse<ExampleResponse>(new ExampleRequest());
                Console.WriteLine("Received response.");
            }
            catch (Exception ex)
            {
                Console.WriteLine("Caught an exception! Success!\n{0}", ex);
            }
        }

        public class ExampleRequest { }
        public class ExampleResponse { }
        public class ExampleRequestHandler : IConsumer<ExampleRequest>
        {
            public Task Consume(ConsumeContext<ExampleRequest> context) => throw new Exception("Request should fail");
        }


        // Synchronization context from Blazor (ASP.NET Core):
        // https://github.com/dotnet/aspnetcore/blob/c74e9efd0e6ea5b4d6737d23b92042772a50a249/src/Components/Components/src/Rendering/RendererSynchronizationContext.cs
        // (removed some unused methods to make it shorter)
        internal class RendererSynchronizationContext : SynchronizationContext
        {
            private static readonly ContextCallback ExecutionContextThunk = (object state) =>
            {
                var item = (WorkItem)state;
                item.SynchronizationContext.ExecuteSynchronously(null, item.Callback, item.State);
            };

            private static readonly Action<Task, object> BackgroundWorkThunk = (Task task, object state) =>
            {
                var item = (WorkItem)state;
                item.SynchronizationContext.ExecuteBackground(item);
            };

            private readonly State _state;

            public event UnhandledExceptionEventHandler UnhandledException;

            public RendererSynchronizationContext()
                : this(new State())
            {
            }

            private RendererSynchronizationContext(State state)
            {
                _state = state;
            }

            public Task InvokeAsync(Func<Task> asyncAction)
            {
                var completion = new RendererSynchronizationTaskCompletionSource<Func<Task>, object>(asyncAction);
                ExecuteSynchronouslyIfPossible(async (state) =>
                {
                    var completion = (RendererSynchronizationTaskCompletionSource<Func<Task>, object>)state;
                    try
                    {
                        await completion.Callback();
                        completion.SetResult(null);
                    }
                    catch (OperationCanceledException)
                    {
                        completion.SetCanceled();
                    }
                    catch (Exception exception)
                    {
                        completion.SetException(exception);
                    }
                }, completion);

                return completion.Task;
            }

            // asynchronously runs the callback
            //
            // NOTE: this must always run async. It's not legal here to execute the work item synchronously.
            public override void Post(SendOrPostCallback d, object state)
            {
                lock (_state.Lock)
                {
                    _state.Task = Enqueue(_state.Task, d, state, forceAsync: true);
                }
            }

            // synchronously runs the callback
            public override void Send(SendOrPostCallback d, object state)
            {
                Task antecedent;
                var completion = new TaskCompletionSource<object>();

                lock (_state.Lock)
                {
                    antecedent = _state.Task;
                    _state.Task = completion.Task;
                }

                // We have to block. That's the contract of Send - we don't expect this to be used
                // in many scenarios in Components.
                //
                // Using Wait here is ok because the antecedent task will never throw.
                antecedent.Wait();

                ExecuteSynchronously(completion, d, state);
            }

            // shallow copy
            public override SynchronizationContext CreateCopy()
            {
                return new RendererSynchronizationContext(_state);
            }

            // Similar to Post, but it can runs the work item synchronously if the context is not busy.
            //
            // This is the main code path used by components, we want to be able to run async work but only dispatch
            // if necessary.
            private void ExecuteSynchronouslyIfPossible(SendOrPostCallback d, object state)
            {
                TaskCompletionSource<object> completion;
                lock (_state.Lock)
                {
                    if (!_state.Task.IsCompleted)
                    {
                        _state.Task = Enqueue(_state.Task, d, state);
                        return;
                    }

                    // We can execute this synchronously because nothing is currently running
                    // or queued.
                    completion = new TaskCompletionSource<object>();
                    _state.Task = completion.Task;
                }

                ExecuteSynchronously(completion, d, state);
            }

            private Task Enqueue(Task antecedent, SendOrPostCallback d, object state, bool forceAsync = false)
            {
                // If we get here is means that a callback is being explicitly queued. Let's instead add it to the queue and yield.
                //
                // We use our own queue here to maintain the execution order of the callbacks scheduled here. Also
                // we need a queue rather than just scheduling an item in the thread pool - those items would immediately
                // block and hurt scalability.
                //
                // We need to capture the execution context so we can restore it later. This code is similar to
                // the call path of ThreadPool.QueueUserWorkItem and System.Threading.QueueUserWorkItemCallback.
                ExecutionContext executionContext = null;
                if (!ExecutionContext.IsFlowSuppressed())
                {
                    executionContext = ExecutionContext.Capture();
                }

                var flags = forceAsync ? TaskContinuationOptions.RunContinuationsAsynchronously : TaskContinuationOptions.None;
                return antecedent.ContinueWith(BackgroundWorkThunk, new WorkItem()
                {
                    SynchronizationContext = this,
                    ExecutionContext = executionContext,
                    Callback = d,
                    State = state,
                }, CancellationToken.None, flags, TaskScheduler.Current);
            }

            private void ExecuteSynchronously(
                TaskCompletionSource<object> completion,
                SendOrPostCallback d,
                object state)
            {
                var original = Current;
                try
                {
                    SetSynchronizationContext(this);
                    _state.IsBusy = true;

                    d(state);
                }
                finally
                {
                    _state.IsBusy = false;
                    SetSynchronizationContext(original);

                    completion?.SetResult(null);
                }
            }

            private void ExecuteBackground(WorkItem item)
            {
                if (item.ExecutionContext == null)
                {
                    try
                    {
                        ExecuteSynchronously(null, item.Callback, item.State);
                    }
                    catch (Exception ex)
                    {
                        DispatchException(ex);
                    }

                    return;
                }

                // Perf - using a static thunk here to avoid a delegate allocation.
                try
                {
                    ExecutionContext.Run(item.ExecutionContext, ExecutionContextThunk, item);
                }
                catch (Exception ex)
                {
                    DispatchException(ex);
                }
            }

            private void DispatchException(Exception ex)
            {
                var handler = UnhandledException;
                if (handler != null)
                {
                    handler(this, new UnhandledExceptionEventArgs(ex, isTerminating: false));
                }
            }

            private class State
            {
                public bool IsBusy; // Just for debugging
                public object Lock = new object();
                public Task Task = Task.CompletedTask;

                public override string ToString()
                {
                    return $"{{ Busy: {IsBusy}, Pending Task: {Task} }}";
                }
            }

            private class WorkItem
            {
                public RendererSynchronizationContext SynchronizationContext;
                public ExecutionContext ExecutionContext;
                public SendOrPostCallback Callback;
                public object State;
            }

            private class RendererSynchronizationTaskCompletionSource<TCallback, TResult> : TaskCompletionSource<TResult>
            {
                public RendererSynchronizationTaskCompletionSource(TCallback callback)
                {
                    Callback = callback;
                }

                public TCallback Callback { get; }
            }
        }
    }
}
