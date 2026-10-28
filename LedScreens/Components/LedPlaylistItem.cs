using Colossal.Serialization.Entities;
using Unity.Collections;
using Unity.Entities;

namespace LedScreens.Components
{
    /// <summary>
    /// A screen's playlist: the media it cycles through when its playlist is on. Added the first
    /// time media is ticked; an empty or missing buffer means "everything in the media folder".
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct LedPlaylistItem : IBufferElementData, ISerializable
    {
        public FixedString128Bytes m_Media;

        public LedPlaylistItem(string media)
        {
            m_Media = default;
            m_Media.CopyFromTruncated(media ?? string.Empty);
        }

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(m_Media.ToString());
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out string media);
            m_Media = default;
            m_Media.CopyFromTruncated(media ?? string.Empty);
        }
    }
}
