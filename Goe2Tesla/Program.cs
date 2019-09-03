using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Client.Options;
using Newtonsoft.Json;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Goe2Tesla
{
    class Program
    {
        private string tokenFile = Environment.GetEnvironmentVariable("TOKEN_FILE");

        private string teslaEmail = Environment.GetEnvironmentVariable("TESLA_EMAIL");
        private string teslaPassword = Environment.GetEnvironmentVariable("TESLA_PASSWORD");
        private string teslaVehicleId = Environment.GetEnvironmentVariable("TESLA_VEHICLE_ID");

        private string mqttServer = Environment.GetEnvironmentVariable("MQTT_SERVER");
        private int mqttPort = int.Parse(Environment.GetEnvironmentVariable("MQTT_PORT"));
        private string mqttUsername = Environment.GetEnvironmentVariable("MQTT_USERNAME");
        private string mqttPassword = Environment.GetEnvironmentVariable("MQTT_PASSWORD");
        private string mqttTopic = Environment.GetEnvironmentVariable("MQTT_TOPIC");

        private string accessToken = null;
        private string refreshToken = null;
        private DateTimeOffset validUntil;

        private bool previousState;
        private bool initialized = false;

        private static ManualResetEvent ResetEvent { get; } = new ManualResetEvent(false);

        static void Main(string[] args)
        {
            Program p = new Program();
            p.Run();

            Program.ResetEvent.WaitOne();
        }

        private async void Run()
        {
            Console.WriteLine("Initializing");
            
            var factory = new MqttFactory();

            using (var mqttClient = factory.CreateMqttClient())
            {
                var options = new MqttClientOptionsBuilder()
                    .WithClientId("Goe2Tesla")
                    .WithTcpServer(mqttServer, mqttPort)
                    .WithCredentials(mqttUsername, mqttPassword)
                    .WithCleanSession()
                    .Build();

                mqttClient.UseDisconnectedHandler(async e =>
                {
                    Console.WriteLine("MQTT connection lost");

                    await Task.Delay(TimeSpan.FromSeconds(10));

                    try
                    {
                        await mqttClient.ConnectAsync(options, CancellationToken.None);
                    }
                    catch
                    {
                        Console.WriteLine("Reconnecting to MQTT server failed");
                    }
                });

                mqttClient.UseApplicationMessageReceivedHandler(async e =>
                {
                    dynamic json = JsonConvert.DeserializeObject(e.ApplicationMessage.ConvertPayloadToString());

                    bool currentState = this.ParseBool(json.alw.ToString());

                    if (this.initialized)
                    {
                        if (currentState && currentState != previousState)
                        {
                            Console.WriteLine("Charging enabled, waking up car...");
                            this.previousState = currentState;

                            try
                            {
                                await this.WakeUp();
                            }
                            catch (TeslaApiException ex)
                            {
                                Console.WriteLine("-------------------------");
                                Console.WriteLine(ex.Message);
                                Console.WriteLine(ex.Status);
                                Console.WriteLine("-------------------------");
                                Console.WriteLine(ex.Response);
                                Console.WriteLine("-------------------------");

                                Console.WriteLine("Clearing token and restarting in 10 seconds");

                                await Task.Delay(TimeSpan.FromSeconds(10));

                                this.ClearToken();
                                Program.ResetEvent.Set();
                            }

                            Console.WriteLine("Woke up car");
                        }
                    }
                    else
                    {
                        this.initialized = true;
                        this.previousState = currentState;
                    }
                });

                mqttClient.UseConnectedHandler(async e =>
                {
                    Console.WriteLine("Connected to server");

                    await mqttClient.SubscribeAsync(mqttTopic);

                    Console.WriteLine("Subscribed to topic");
                });

                await mqttClient.ConnectAsync(options, CancellationToken.None);
            }
        }

        private bool ParseBool(string input)
        {
            return (input == "1");
        }

        private async Task WakeUp()
        {
            if (accessToken == null && File.Exists(tokenFile))
            {
                Console.WriteLine("Reading token from token file");
                this.ParseToken(JsonConvert.DeserializeObject(await File.ReadAllTextAsync(tokenFile)));
            }

            if (accessToken == null)
            {
                Console.WriteLine("Logging into Tesla API...");

                await this.Login();

                Console.WriteLine("Logged into API");
            }
            else if (DateTimeOffset.Now >= this.validUntil)
            {
                Console.WriteLine("Refreshing Tesla token...");

                await this.Refresh();

                Console.WriteLine("Refreshed token");
            }

            using (HttpClient client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("User-Agent", "Goe2Tesla/1.0");
                client.DefaultRequestHeaders.Add("Authorization", "Bearer " + this.accessToken);

                using (HttpResponseMessage response = await client.PostAsync(string.Format("https://owner-api.teslamotors.com/api/1/vehicles/{0}/wake_up", this.teslaVehicleId), new StringContent("")))
                {
                    string json = await response.Content.ReadAsStringAsync();

                    if (response.StatusCode != HttpStatusCode.OK)
                        throw new TeslaApiException("Wake up failed", response.StatusCode, json);
                }
            }
        }

        private async Task Login()
        {
            using (HttpClient client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("User-Agent", "Goe2Tesla/1.0");

                dynamic request = new
                {
                    grant_type = "password",
                    client_id = "81527cff06843c8634fdc09e8ac0abefb46ac849f38fe1e431c2ef2106796384",
                    client_secret = "c7257eb71a564034f9419ee651c7d0e5f7aa6bfbd18bafb5c5c033b093bb2fa3",
                    email = this.teslaEmail,
                    password = this.teslaPassword
                };

                using (HttpResponseMessage response = await client.PostAsync("https://owner-api.teslamotors.com/oauth/token?grant_type=password", new StringContent(JsonConvert.SerializeObject(request), Encoding.UTF8, "application/json")))
                {
                    string json = await response.Content.ReadAsStringAsync();

                    if (response.StatusCode != HttpStatusCode.OK)
                        throw new TeslaApiException("Login failed", response.StatusCode, json);

                    await File.WriteAllTextAsync(tokenFile, json);

                    this.ParseToken(JsonConvert.DeserializeObject(json));
                }
            }
        }

        private async Task Refresh()
        {
            using (HttpClient client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("User-Agent", "Goe2Tesla/1.0");

                dynamic request = new
                {
                    grant_type = "refresh_token",
                    client_id = "81527cff06843c8634fdc09e8ac0abefb46ac849f38fe1e431c2ef2106796384",
                    client_secret = "c7257eb71a564034f9419ee651c7d0e5f7aa6bfbd18bafb5c5c033b093bb2fa3",
                    refresh_token = this.refreshToken
                };

                using (HttpResponseMessage response = await client.PostAsync("https://owner-api.teslamotors.com/oauth/token?grant_type=refresh_token", new StringContent(JsonConvert.SerializeObject(request), Encoding.UTF8, "application/json")))
                {
                    string json = await response.Content.ReadAsStringAsync();

                    if (response.StatusCode != HttpStatusCode.OK)
                        throw new TeslaApiException("Token refresh failed", response.StatusCode, json);

                    await File.WriteAllTextAsync(tokenFile, json);

                    this.ParseToken(JsonConvert.DeserializeObject(json));
                }
            }
        }

        private void ParseToken(dynamic json)
        {
            this.accessToken = json.access_token.ToString();
            this.refreshToken = json.refresh_token.ToString();

            this.validUntil = DateTimeOffset
                .FromUnixTimeSeconds(long.Parse(json.created_at.ToString()))
                .AddSeconds(double.Parse(json.expires_in.ToString()))
                .AddHours(-1);
        }

        private void ClearToken()
        {
            this.accessToken = null;
            this.refreshToken = null;

            this.validUntil = DateTimeOffset.Now;

            File.Delete(tokenFile);
        }
    }
}
