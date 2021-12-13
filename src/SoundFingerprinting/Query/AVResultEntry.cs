namespace SoundFingerprinting.Query
{
    using System;
    using SoundFingerprinting.DAO.Data;
    using SoundFingerprinting.Data;

    /// <summary>
    ///  Class that contains audio/video result entry.
    /// </summary>
    public class AVResultEntry
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="AVResultEntry"/> class.
        /// </summary>
        /// <param name="audio">Audio result.</param>
        /// <param name="video">Video result.</param>
        /// <exception cref="ArgumentException">Argument exception in case both audio and video results are null.</exception>
        public AVResultEntry(ResultEntry? audio, ResultEntry? video)
        {
            if (audio == null && video == null)
            {
                throw new ArgumentException($"Both {nameof(audio)} and {nameof(video)} result entries cannot be null at the same time.");
            }

            Audio = audio;
            Video = video;
        }

        /// <summary>
        ///  Gets audio result entry.
        /// </summary>
        public ResultEntry? Audio { get; }

        /// <summary>
        ///  Gets video result entry.
        /// </summary>
        public ResultEntry? Video { get; }
        
        /// <summary>
        ///  Gets track id.
        /// </summary>
        public string TrackId => TrackData.Id;
        
        /// <summary>
        ///  Gets matched at date.
        /// </summary>
        /// <remarks>
        ///  When both audio and video matches MatchedAt will be equal to minimum between audio and video matched date.
        /// </remarks>
        public DateTime MatchedAt
        {
            get
            {
                return (Audio, Video) switch
                {
                    (null, _) => Video!.MatchedAt,
                    (_, null) => Audio!.MatchedAt,
                    (_, _) => new DateTime(Math.Min(Audio!.MatchedAt.Ticks, Video!.MatchedAt.Ticks))
                };
            }
        }

        /// <summary>
        ///  Converts to audio video query match object that you can register in the registry service <see cref="IQueryMatchRegistry"/>.
        /// </summary>
        /// <param name="streamId">Stream identifier.</param>
        /// <param name="playbackUrl">Match playback URL.</param>
        /// <param name="reviewStatus">review status.</param>
        /// <returns>An instance of <see cref="AVQueryMatch"/>.</returns>
        public AVQueryMatch ConvertToAvQueryMatch(string streamId = "", string playbackUrl = "", ReviewStatus reviewStatus = ReviewStatus.None)
        {
            return new AVQueryMatch(Guid.NewGuid().ToString(), ToQueryMatch(Audio), ToQueryMatch(Video), streamId, playbackUrl, reviewStatus);
        }
        
        /// <summary>
        ///  Deconstruct <see cref="AVResultEntry"/>.
        /// </summary>
        /// <param name="audio">Audio result entry.</param>
        /// <param name="video">Video result entry.</param>
        public void Deconstruct(out ResultEntry? audio, out ResultEntry? video)
        {
            audio = Audio;
            video = Video;
        }

        private static QueryMatch? ToQueryMatch(ResultEntry? resultEntry)
        {
            if (resultEntry == null)
            {
                return null;
            }
            
            var track = new TrackInfo(resultEntry.Track.Id, resultEntry.Track.Title, resultEntry.Track.Artist, resultEntry.Track.MetaFields, resultEntry.Track.MediaType);
            return new QueryMatch(Guid.NewGuid().ToString(), track, resultEntry.Coverage, resultEntry.MatchedAt);
        }

        private TrackData TrackData => (Audio ?? Video)!.Track;
    }
}