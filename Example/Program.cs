using System;
using System.Threading;
using System.Threading.Tasks;
using SimpleJsonService;

namespace Example
{
    class Program
    {
        static void Main(string[] args)
        {
            var tokenSource = new CancellationTokenSource();

            Console.CancelKeyPress += (s,e) => {
                tokenSource.Cancel();
                e.Cancel = true;
            };

            var serviceHost = new JsonServiceHost(new Uri("http://localhost:38080/"), new ExampleController());

            try
            {
                serviceHost.Start();
                Task.Delay(-1).Wait(tokenSource.Token);
            }
            catch (OperationCanceledException)
            {
                serviceHost.Stop();
            }
        }
    }
}
