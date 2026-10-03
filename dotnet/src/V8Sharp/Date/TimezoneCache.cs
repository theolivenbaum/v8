// Port of src/base/timezone-cache.h and of the POSIX time zone cache V8 uses
// without ICU (PosixTimezoneCache in src/base/platform/platform-posix.cc,
// PosixDefaultTimezoneCache in platform-posix-time.cc), and the isolate's
// date cache (Isolate::date_cache, date_cache_stamp).
namespace V8Sharp.Date
{
    /// <summary>V8's base::TimezoneCache.</summary>
    public abstract class TimezoneCache
    {
        /// <summary>
        /// Time zone redetection indicator for Clear: kSkip indicates the host
        /// time zone doesn't have to be redetected; kRedetect that it should be.
        /// </summary>
        public enum TimeZoneDetection { kSkip, kRedetect }

        /// <summary>Short name of the local timezone (e.g., EST).</summary>
        public abstract string LocalTimezone(double timeMs);

        /// <summary>https://tc39.es/ecma262/#sec-daylight-saving-time-adjustment</summary>
        public abstract double DaylightSavingsOffset(double timeMs);

        /// <summary>https://tc39.es/ecma262/#sec-local-time-zone-adjustment</summary>
        public abstract double LocalTimeOffset(double timeMs, bool isUtc);

        /// <summary>Called when the local timezone changes.</summary>
        public abstract void Clear(TimeZoneDetection timeZoneDetection);
    }

    /// <summary>
    /// PosixDefaultTimezoneCache: localtime_r over the host time zone.
    /// Deviation: V8 asks the C library (localtime_r, which reads TZ and the
    /// tz database); V8Sharp asks TimeZoneInfo.Local, which reads the same TZ
    /// variable and database on Linux. The offsets agree; the time zone name
    /// is TimeZoneInfo's StandardName/DaylightName, which on Unix are the tz
    /// database abbreviations localtime_r reports as tm_zone ("EST", "EDT",
    /// "GMT", "BST"), except for the UTC zones, where .NET reports
    /// "Coordinated Universal Time" and V8Sharp substitutes the abbreviation,
    /// and for historical abbreviations (LMT, EWT...) that .NET does not keep.
    /// </summary>
    public sealed class PosixDefaultTimezoneCache : TimezoneCache
    {
        const int msPerSecond = 1000;

        TimeZoneInfo _local = TimeZoneInfo.Local;

        DateTimeOffset ToDateTime(double timeMs)
        {
            // time_t tv = static_cast<time_t>(std::floor(time / msPerSecond));
            double seconds = Math.Floor(timeMs / msPerSecond);
            seconds = Math.Clamp(seconds, -62135596800.0, 253402300799.0);
            return DateTimeOffset.FromUnixTimeSeconds((long)seconds);
        }

        public override string LocalTimezone(double time)
        {
            if (double.IsNaN(time)) return "";
            DateTimeOffset t = ToDateTime(time);
            bool isDst = _local.IsDaylightSavingTime(t);
            string name = isDst ? _local.DaylightName : _local.StandardName;
            if (name == "Coordinated Universal Time" || name.Length == 0) name = UtcAbbreviation(_local.Id);
            return name;
        }

        static string UtcAbbreviation(string id) =>
            id.Contains("GMT", StringComparison.Ordinal) || id.Contains("Greenwich", StringComparison.Ordinal) ? "GMT" : "UTC";

        /// <summary>PosixTimezoneCache::DaylightSavingsOffset: one hour when tm_isdst, else 0.</summary>
        public override double DaylightSavingsOffset(double time)
        {
            if (double.IsNaN(time)) return double.NaN;
            return _local.IsDaylightSavingTime(ToDateTime(time)) ? 3600 * msPerSecond : 0;
        }

        /// <summary>
        /// PosixDefaultTimezoneCache::LocalTimeOffset: preserves the old
        /// behavior for non-ICU implementation by ignoring both time_ms and
        /// is_utc: the current offset without the daylight savings hour.
        /// </summary>
        public override double LocalTimeOffset(double timeMs, bool isUtc)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            TimeSpan gmtoff = _local.GetUtcOffset(now);
            bool isDst = _local.IsDaylightSavingTime(now);
            // tm_gmtoff includes any daylight savings offset, so subtract it.
            return (long)gmtoff.TotalSeconds * msPerSecond - (isDst ? 3600 * msPerSecond : 0);
        }

        public override void Clear(TimeZoneDetection timeZoneDetection)
        {
            if (timeZoneDetection == TimeZoneDetection.kRedetect)
            {
                TimeZoneInfo.ClearCachedData();
                _local = TimeZoneInfo.Local;
            }
        }
    }
}

namespace V8Sharp
{
    public sealed partial class Isolate
    {
        Date.DateCache? _dateCache;
        int _dateCacheStamp;
        bool _isDateCacheUsed;

        /// <summary>Isolate::date_cache.</summary>
        public Date.DateCache DateCache
        {
            get => _dateCache ??= new Date.DateCache();
            set => _dateCache = value;
        }

        /// <summary>Isolate::date_cache_stamp.</summary>
        public int DateCacheStamp => _dateCacheStamp;

        /// <summary>
        /// Isolate::GetDateCacheStampAndRecordUsage: the current stamp, noting
        /// that a JSDate cached fields under it.
        /// </summary>
        public int GetDateCacheStampAndRecordUsage()
        {
            _isDateCacheUsed = true;
            return _dateCacheStamp;
        }

        /// <summary>Isolate::IncreaseDateCacheStampAndInvalidateProtector.</summary>
        public void IncreaseDateCacheStampAndInvalidateProtector()
        {
            // There's no need to update stamp and invalidate the protector since there
            // were no JSDate instances created yet and thus such a configuration change
            // is not observable anyway.
            if (!_isDateCacheUsed) return;
            _dateCacheStamp = _dateCacheStamp < JSValue.SmiMaxValue ? _dateCacheStamp + 1 : 1;
            if (Protectors.IsNoDateTimeConfigurationChangeIntact(this))
            {
                Protectors.InvalidateNoDateTimeConfigurationChange(this);
            }
        }

        /// <summary>
        /// v8::Isolate::DateTimeConfigurationChangeNotification: resets the
        /// date cache after the host time zone changed.
        /// </summary>
        public void DateTimeConfigurationChangeNotification(Date.TimezoneCache.TimeZoneDetection detection)
        {
            DateCache.ResetDateCache(detection);
            IncreaseDateCacheStampAndInvalidateProtector();
        }
    }
}
