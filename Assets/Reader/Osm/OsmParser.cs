using System.Collections.Generic;
using OsmSharp;
using OsmSharp.Tags;
using UnityEngine;

/// <summary>
/// Converts raw OsmGeo elements into typed ParsedWay structs.
/// Node Y positions stay at 0 — elevation is applied by RoadMesher
/// directly from SRTMHeightmap at mesh build time.
/// </summary>
public static class OsmParser
{
    public static TileData Parse(List<OsmGeo> elements)
    {
        var data = new TileData();
        if (elements == null || elements.Count == 0) return data;

        // First pass — index nodes AND capture natural=tree point features
        var nodeIndex = new Dictionary<long, ParsedNode>();

        foreach (OsmGeo element in elements)
        {
            if (element is Node node && node.Id.HasValue &&
                node.Latitude.HasValue && node.Longitude.HasValue)
            {
                var parsed = new ParsedNode
                {
                    Id       = node.Id.Value,
                    Lat      = node.Latitude.Value,
                    Lon      = node.Longitude.Value,
                    WorldPos = Mercator.ToWorld(node.Latitude.Value, node.Longitude.Value)
                };
                nodeIndex[parsed.Id] = parsed;

                // Capture surveyed tree positions
                if (node.Tags != null &&
                    node.Tags.TryGetValue("natural", out string natural) &&
                    natural == "tree")
                {
                    data.Trees.Add(parsed);
                }
            }
        }

        // Second pass — build ways
        foreach (OsmGeo element in elements)
        {
            if (element is Way way && way.Id.HasValue && way.Nodes != null)
            {
                ParsedWay parsed = BuildWay(way, nodeIndex);
                if (parsed == null) continue;

                switch (parsed.WayType)
                {
                    case WayType.Road:     data.Roads.Add(parsed);     break;
                    case WayType.Landmass: data.Landmass.Add(parsed);  break;
                    case WayType.Water:    data.Water.Add(parsed);     break;
                    case WayType.Building: data.Buildings.Add(parsed); break;
                    default:               data.Unknown.Add(parsed);   break;
                }
            }
        }

        return data;
    }

    // --- Private ---

    private static ParsedWay BuildWay(Way way, Dictionary<long, ParsedNode> nodeIndex)
    {
        var points  = new List<Vector2>(way.Nodes.Length);
        var latLons = new List<Vector2d>(way.Nodes.Length);

        foreach (long nodeId in way.Nodes)
        {
            if (!nodeIndex.TryGetValue(nodeId, out ParsedNode node)) continue;
            points.Add(new Vector2(node.WorldPos.x, node.WorldPos.z));
            latLons.Add(new Vector2d(node.Lat, node.Lon));
        }

        if (points.Count < 2) return null;

        TagsCollectionBase tags   = way.Tags ?? new TagsCollection();
        bool               closed = way.Nodes[0] == way.Nodes[way.Nodes.Length - 1];

        double minLat = double.MaxValue, maxLat = double.MinValue;
        double minLon = double.MaxValue, maxLon = double.MinValue;

        foreach (var ll in latLons)
        {
            if (ll.x < minLat) minLat = ll.x;
            if (ll.x > maxLat) maxLat = ll.x;
            if (ll.y < minLon) minLon = ll.y;
            if (ll.y > maxLon) maxLon = ll.y;
        }

        return new ParsedWay
        {
            Id       = way.Id.Value,
            Points   = points,
            LatLons  = latLons,
            Tags     = FlattenTags(tags),
            WayType  = ClassifyWay(tags),
            IsClosed = closed,
            MinLat   = minLat,
            MaxLat   = maxLat,
            MinLon   = minLon,
            MaxLon   = maxLon
        };
    }

    private static WayType ClassifyWay(TagsCollectionBase tags)
    {
        if (tags.ContainsKey("highway"))  return WayType.Road;
        if (tags.ContainsKey("building")) return WayType.Building;
        if (tags.ContainsKey("waterway")) return WayType.Water;

        if (tags.TryGetValue("natural", out string natVal))
        {
            switch (natVal)
            {
                case "water": case "wetland": case "spring": case "reef":
                    return WayType.Water;
                case "bare_rock": case "wood": case "forest": case "grassland":
                case "grass": case "scrub": case "heath": case "fell":
                case "beach": case "sand": case "coastline": case "cliff":
                case "rock": case "scree": case "mud": case "floodplain":
                case "ridge": case "ravine": case "landslide": case "gravel":
                    return WayType.Landmass;
            }
        }

        if (tags.TryGetValue("landuse", out string luVal))
        {
            switch (luVal)
            {
                case "forest": case "farmland": case "farmyard": case "grass":
                case "greenfield": case "brownfield": case "residential":
                case "commercial": case "industrial": case "retail":
                case "construction": case "cemetery": case "military":
                case "recreation_ground": case "meadow": case "common":
                case "landfill": case "quarry": case "reservoir": case "wharf":
                    return WayType.Landmass;
            }
        }

        if (tags.ContainsKey("amenity"))  return WayType.Landmass;
        if (tags.ContainsKey("leisure"))  return WayType.Landmass;
        if (tags.ContainsKey("tourism"))  return WayType.Landmass;
        if (tags.ContainsKey("aeroway"))  return WayType.Landmass;

        return WayType.Unknown;
    }

    private static Dictionary<string, string> FlattenTags(TagsCollectionBase tags)
    {
        var dict = new Dictionary<string, string>();
        foreach (var tag in tags)
            dict[tag.Key] = tag.Value;
        return dict;
    }
}
