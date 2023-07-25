﻿namespace StreamMasterDomain.Entities;

public class StreamGroup : BaseEntity
{
    public StreamGroup()
    {
        ChildVideoStreams = new List<StreamGroupVideoStream>();
        ChannelGroups = new List<StreamGroupChannelGroup>();
    }

    public ICollection<StreamGroupChannelGroup> ChannelGroups { get; set; }
    public ICollection<StreamGroupVideoStream> ChildVideoStreams { get; set; }

    public string Name { get; set; } = string.Empty;
    public int StreamGroupNumber { get; set; }
}
