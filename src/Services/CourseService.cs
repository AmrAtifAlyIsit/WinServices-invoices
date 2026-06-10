using MongoDB.Bson;
using MongoDB.Driver;

namespace EthraiOrderFixService.Services;

public record CourseInfo(string CourseName, int OracleId);

public class CourseService
{
    private readonly IMongoCollection<BsonDocument> _CourseCollection;

    public CourseService(IMongoClient client)
    {
        var CourseDb = client.GetDatabase("coursesdb");
        _CourseCollection = CourseDb.GetCollection<BsonDocument>("course");
    }

    public async Task<CourseInfo?> GetCourseInfoByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        var filter = Builders<BsonDocument>.Filter.Eq("_id", id);
        var doc = await _CourseCollection.Find(filter).FirstOrDefaultAsync(cancellationToken);
        if (doc == null)
            return null;

        var name = doc.Contains("NameAr") ? doc["NameAr"].ToString() : doc.GetValue("_id").ToString();
        int oracleId = doc.Contains("OracleId") ? doc["OracleId"].ToInt32() : 0;

        return new CourseInfo(name ?? string.Empty, oracleId);
    }
}
