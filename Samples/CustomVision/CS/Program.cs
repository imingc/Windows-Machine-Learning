using Microsoft.Azure.Devices.Client;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Loader;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.AI.MachineLearning;
using Windows.Foundation;
using Windows.Media;

using EdgeModuleSamples.Common;
using static EdgeModuleSamples.Common.AsyncHelper;
using static Helpers.BlockTimerHelper;

namespace SampleModule
{
    class ImageInference
    {
        private static AppOptions Options;
        private static ModuleClient ioTHubModuleClient;
        private static ScoringModel Model = null;
        private static CancellationTokenSource cts = null;

        static async Task<int> Main(string[] args)
        {
            try
            {
                //
                // Parse options
                //

                Options = new AppOptions();

                Options.Parse(args);

                if (Options.Exit)
                    return -1;

                if (string.IsNullOrEmpty(Options.DeviceId) && string.IsNullOrEmpty(Options.ImagesDir))
                    throw new ApplicationException("Please use --device to specify which camera to use or --imagedir to specify a set of images");

                //
                // Init module client
                //

                if (Options.UseEdge)
                {
                    Log.WriteLine($"{AppOptions.AppName} module starting.");
                    await BlockTimer("Initializing Azure IoT Edge", async () => await InitEdge());
                }

                cts = new CancellationTokenSource();
                AssemblyLoadContext.Default.Unloading += (ctx) => cts.Cancel();
                Console.CancelKeyPress += (sender, cpe) => cts.Cancel();

                //
                // Load model
                //

                await BlockTimer($"Loading modelfile '{Options.ModelPath}' on the {(Options.UseGpu ? "GPU" : "CPU")}",
                    async () => {
                        var d = Directory.GetCurrentDirectory();
                        var path = d + "\\" + Options.ModelPath;
                        Log.WriteLineVerbose($"Model path: {path}");
                        StorageFile modelFile = await AsAsync(StorageFile.GetFileFromPathAsync(path));                       
                        Model = await ScoringModel.CreateFromStreamAsync(modelFile,Options.UseGpu);
                    });

                //
                // Image loop
                //

                if (!string.IsNullOrEmpty(Options.ImagesDir))
                {
                    var path = Directory.GetCurrentDirectory() + "\\" + Options.ImagesDir;
                    var files = Directory.GetFiles(path);
                    foreach (var file in files)
                    {
                        Log.WriteLineVerbose($"Opening {file}...");
                        StorageFile imageFile = await AsAsync(StorageFile.GetFileFromPathAsync(file));
                        using (IRandomAccessStream stream = await AsAsync(imageFile.OpenAsync(FileAccessMode.Read)))                       
                        {
                            Log.WriteLineCurrentLine();

                            // Create the decoder from the stream
                            var decoder = await AsAsync(BitmapDecoder.CreateAsync(stream));
                            var transform = new BitmapTransform();

                            // Get the SoftwareBitmap representation of the file
                            //var softwareBitmap = await AsAsync(decoder.GetSoftwareBitmapAsync());
                            var softwareBitmap = await AsAsync(
                                decoder.GetSoftwareBitmapAsync(
                                    BitmapPixelFormat.Rgba8,
                                    BitmapAlphaMode.Ignore,
                                    transform,
                                    ExifOrientationMode.RespectExifOrientation,
                                    ColorManagementMode.DoNotColorManage));

                            // Convert to a video frame
                            var inputImage = VideoFrame.CreateWithSoftwareBitmap(softwareBitmap);

                            // Process it
                            await DoProcessFrame(inputImage);
                        }
                    }
                }

                return 0;
            }
            catch (Exception ex)
            {
                Log.WriteLineException(ex);
                return -1;
            }
        }
        
        private static async Task DoProcessFrame(VideoFrame inputImage)
        {
            ImageFeatureValue imageTensor = ImageFeatureValue.CreateFromVideoFrame(inputImage);

            //
            // Evaluate model
            //

            ScoringOutput outcome = null;
            var evalticks = await BlockTimer("Running the model",
                async () =>
                {
                    var input = new ScoringInput() { data = imageTensor };
                    outcome = await Model.EvaluateAsync(input);
                });

            //
            // Print results
            //

            var message = ResultsToMessage(outcome);
            message.metrics.evaltimeinms = evalticks;
            var json = JsonConvert.SerializeObject(message);
            Log.WriteLineRaw($"Recognized {json}");

            //
            // Send results to Edge
            //

            if (Options.UseEdge)
            { 
                var eventMessage = new Message(Encoding.UTF8.GetBytes(json));
                await ioTHubModuleClient.SendEventAsync("resultsOutput", eventMessage); 

                // Let's not totally spam Edge :)
                await Task.Delay(500);
            }
        }

        private static MessageBody ResultsToMessage(ScoringOutput outcome)
        {
            var resultVector = outcome.classLabel.GetAsVectorView();
            var message = new MessageBody();
            message.results = new LabelResult[1];
            message.results[0] = new LabelResult() { label = resultVector.First(), confidence = 1.0 };

            return message;
        }
        
        /// <summary>
        /// Initializes the ModuleClient and sets up the callback to receive
        /// messages containing temperature information
        /// </summary>
        private static async Task InitEdge()
        {
            // Open a connection to the Edge runtime
            ioTHubModuleClient = await ModuleClient.CreateFromEnvironmentAsync(TransportType.Amqp);

            Log.WriteLineVerbose("CreateFromEnvironmentAsync OK");

            await ioTHubModuleClient.OpenAsync();

            Log.WriteLineVerbose("OpenAsync OK");

            Log.WriteLine($"IoT Hub module client initialized.");
        }

        /// <summary>
        /// Handles cleanup operations when app is cancelled or unloads
        /// </summary>
        public static Task WhenCancelled(CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<bool>();
            cancellationToken.Register(s => ((TaskCompletionSource<bool>)s).SetResult(true), tcs);
            return tcs.Task;
        }
    }
}