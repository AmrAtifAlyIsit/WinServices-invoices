using MongoDB.Bson;
using MongoDB.Driver;

namespace EthraiOrderFixService.Services;

public class EnrollmentService
{
    private readonly IMongoCollection<BsonDocument> _enrollmentCollection;

    public EnrollmentService(IMongoCollection<BsonDocument> enrollmentCollection)
    {
        _enrollmentCollection = enrollmentCollection;
    }

    // Add methods if needed later
}
