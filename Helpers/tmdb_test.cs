using System;
using System.Linq;
using System.Threading.Tasks;
using TMDbLib.Client;

class Program
{
    static async Task Main()
    {
        var client = new TMDbClient("f6bd687ffa63cd282b6ff2c6877f2669");
        
        string[] titles = { "Ramba Oorvasi Menaka", "Rambha Urvasi Menaka", "S. Saraswathi", "Normal", "Pallaburusu", "Pallabhurusu" };
        foreach (var t in titles)
        {
            var res = await client.SearchMovieAsync(t);
            Console.WriteLine($"Search '{t}': {res.Results.Count} results");
            if (res.Results.Count > 0) {
                var first = res.Results.First();
                Console.WriteLine($" -> Found: {first.Title} (OriginalLang: {first.OriginalLanguage})");
            }
        }
    }
}
