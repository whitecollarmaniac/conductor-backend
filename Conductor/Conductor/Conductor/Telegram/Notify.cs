namespace Conductor.Telegram
{
    public static class Notify
    {
        public static async Task SendMessageAsync(string token, string chatId, string message)
        {
            using var client = new HttpClient();
            var url = $"https://api.telegram.org/bot{token}/sendMessage";
            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("chat_id", chatId),
                new KeyValuePair<string, string>("text", message)
            });
            var response = await client.PostAsync(url, content);
            response.EnsureSuccessStatusCode();
        }
    }
}
