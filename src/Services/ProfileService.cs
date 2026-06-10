using MongoDB.Bson;
using MongoDB.Driver;

namespace EthraiOrderFixService.Services;

public class ProfileService
{
    private readonly IMongoCollection<BsonDocument> _profileCollection;

    public ProfileService(IMongoClient client)
    {
        var profileDb = client.GetDatabase("profiledb");
        _profileCollection = profileDb.GetCollection<BsonDocument>("profile");
    }

    // Returns a string identifier for trainee - adapt to your real model
    public async Task<string?> GetProfileIdentificationByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        var filter = Builders<BsonDocument>.Filter.Eq("_id", id);
        var doc = await _profileCollection.Find(filter).FirstOrDefaultAsync(cancellationToken);
        if (doc == null) return null;

        // Example: return NationalId or _id as string - change as needed
        if (doc.Contains("NationalId")) return doc["NationalId"].ToString();
        return doc.GetValue("_id").ToString();
    }
}
