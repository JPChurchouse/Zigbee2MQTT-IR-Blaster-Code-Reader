using MQTTnet;
using MQTTnet;
using MQTTnet.Protocol;
using System.Text;
using System.Text.Json;
using MQTTnet.Protocol;
using System.Text;
using System.Text.Json;

const string Broker = "192.168.1.23";
const int Port = 1883;

const string CommandTopic = "zigbee2mqtt/Lodge IR Blaster/set/learn_ir_code";
const string ResultTopic = "zigbee2mqtt/Lodge IR Blaster";

var codes = new Dictionary<string, string>();

var modes = new[]
{
    "cool",
    "heat"
};

var temperatures = Enumerable.Range(18, 9);

var mqttFactory = new MqttClientFactory();
var client = mqttFactory.CreateMqttClient();

string? pendingCode = null;

client.ApplicationMessageReceivedAsync += e =>
{
    try
    {
        var payload = Encoding.UTF8.GetString(e.ApplicationMessage.Payload);

        using var doc = JsonDocument.Parse(payload);

        if (doc.RootElement.TryGetProperty("learned_ir_code", out var codeElement))
        {
            pendingCode = codeElement.GetString();

            Console.WriteLine();
            Console.WriteLine("=== IR CODE RECEIVED ===");
            Console.WriteLine(pendingCode);
            Console.WriteLine();
        }
    }
    catch
    {
        // Ignore malformed payloads
    }

    return Task.CompletedTask;
};

var options = new MqttClientOptionsBuilder()
    .WithTcpServer(Broker, Port)
    .WithCredentials("mqtt_username", "mqtt_password")
    .WithCleanSession()
    .Build();

Console.WriteLine("Connecting to MQTT...");
await client.ConnectAsync(options);

Console.WriteLine("Connected.");

await client.SubscribeAsync(ResultTopic);

Console.WriteLine($"Subscribed to: {ResultTopic}");
Console.WriteLine();

foreach (var mode in modes)
{
    foreach (var temp in temperatures)
    {
        var key = $"{mode}_{temp}";

        Console.WriteLine("======================================");
        Console.WriteLine($"SET HEAT PUMP TO: {mode.ToUpper()} {temp}°C");
        Console.WriteLine("Then press ENTER to arm IR learning.");
        Console.WriteLine("======================================");

        Console.ReadLine();

        pendingCode = null;

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(CommandTopic)
            .WithPayload("ON")
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

        await client.PublishAsync(message);

        Console.WriteLine("Learning armed.");
        Console.WriteLine("Press the button on the Toshiba remote NOW...");
        Console.WriteLine();

        var timeout = DateTime.UtcNow.AddSeconds(15);

        while (pendingCode == null && DateTime.UtcNow < timeout)
        {
            await Task.Delay(100);
        }

        if (pendingCode == null)
        {
            Console.WriteLine("TIMEOUT - No IR code received.");
            continue;
        }

        codes[key] = pendingCode;

        Console.WriteLine($"Stored: {key}");
        Console.WriteLine();
    }
}

Console.WriteLine("Capturing OFF code...");
Console.WriteLine("Set remote to OFF and press ENTER.");

Console.ReadLine();

pendingCode = null;

await client.PublishAsync(
    new MqttApplicationMessageBuilder()
        .WithTopic(CommandTopic)
        .WithPayload("ON")
        .Build());

var offTimeout = DateTime.UtcNow.AddSeconds(15);

while (pendingCode == null && DateTime.UtcNow < offTimeout)
{
    await Task.Delay(100);
}

if (pendingCode != null)
{
    codes["off"] = pendingCode;
}

Console.WriteLine();
Console.WriteLine("Generating Home Assistant YAML...");
Console.WriteLine();

var yaml = new StringBuilder();

yaml.AppendLine("script:");

foreach (var kvp in codes)
{
    yaml.AppendLine($"  hp_{kvp.Key}:");
    yaml.AppendLine($"    alias: Heat Pump {kvp.Key}");
    yaml.AppendLine($"    sequence:");
    yaml.AppendLine($"      - action: mqtt.publish");
    yaml.AppendLine($"        data:");
    yaml.AppendLine($"          topic: zigbee2mqtt/Lodge IR Blaster/set");
    yaml.AppendLine($"          payload: >");
    yaml.AppendLine($"            {{\"ir_code_to_send\":\"{kvp.Value}\"}}");
    yaml.AppendLine();
}

File.WriteAllText("heatpump_scripts.yaml", yaml.ToString());

var json = JsonSerializer.Serialize(codes, new JsonSerializerOptions
{
    WriteIndented = true
});

File.WriteAllText("heatpump_codes.json", json);

Console.WriteLine("Done.");
Console.WriteLine("Generated:");
Console.WriteLine("- heatpump_scripts.yaml");
Console.WriteLine("- heatpump_codes.json");

await client.DisconnectAsync();