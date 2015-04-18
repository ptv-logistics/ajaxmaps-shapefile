using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Web;
using System.Web.Services;
using SharpMap;
using SharpMap.CoordinateSystems;
using SharpMap.CoordinateSystems.Transformations;
using SharpMap.Data;
using SharpMap.Data.Providers;
using SharpMap.Geometries;
using SharpMap.Layers;
using SharpMap.Rendering.Thematics;
using SharpMap.Styles;
using SharpMap.Utilities;

namespace ShapeTiles
{
    /// <summary>
    /// Genereates tiles which show world countries colored by popuplation density
    /// The handler utilizes SharpMap http://sharpmap.codeplex.com to render shape files
    /// The request parameters (x,y,z) are the numbers according to the Google/Bing tiling scheme
    /// </summary>
    [WebService(Namespace = "http://tempuri.org/")]
    [WebServiceBinding(ConformsTo = WsiProfiles.BasicProfile1_1)]
    public class ShapeTileHandler : IHttpHandler
    {
        #region rendering

        public void ProcessRequest(HttpContext context)
        {
            // tile keys
            int x, y, z;

            //Parse request parameters
            if (!int.TryParse(context.Request.Params["x"], out x))
                throw (new ArgumentException("Invalid parameter"));
            if (!int.TryParse(context.Request.Params["y"], out y))
                throw (new ArgumentException("Invalid parameter"));
            if (!int.TryParse(context.Request.Params["z"], out z))
                throw (new ArgumentException("Invalid parameter"));
            string layer = context.Request.Params["layer"]; // not used here
            string style = context.Request.Params["style"]; // not used here

            // set response type to png
            context.Response.ContentType = "image/png";

            // check if already rendered, rendered tiles are cached within HttpContext
            string cacheKey = string.Format("Tile/{0}/{1}/{2}/{3}/{4}", layer, style, x, y, z);
            byte[] buffer = context.Cache[cacheKey] as byte[];
            if (buffer != null)
            {
                context.Response.OutputStream.Write(buffer, 0, buffer.Length);
                return;
            }

            // create a transparent sharpmap map with a size of 256x256
            using (var sharpMap = new Map(new Size(256, 256)) { BackColor = Color.Transparent })
            {
                // the map contains only one layer
                var countries = new VectorLayer("WorldCountries")
                {
                    // set tranform to WGS84->Spherical_Mercator
                    CoordinateTransformation = TransformToMercator(GeographicCoordinateSystem.WGS84),

                    // set the sharpmap provider for shape files as data source
                    DataSource = new ShapeFile(
                        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "App_Data") +
                        @"\world_countries_boundary_file_world_2002.shp"),

                    // use a dynamic style for thematic mapping
                    // the lambda also takes the map instance into account (to scale the border width)
                    Theme = new CustomTheme(row => GetPopDensStyle(sharpMap, row)),
                };

                // add the layer to the map
                sharpMap.Layers.Add(countries);

                // calculate the bbox for the tile key and zoom the map 
                sharpMap.ZoomToBox(TileToMercatorAtZoom(x, y, z));

                // render the map image
                using (var img = sharpMap.GetMap())
                {
                    // stream the image to the client
                    using (var memoryStream = new MemoryStream())
                    {
                        // Saving a PNG image requires a seekable stream, first save to memory stream 
                        // http://forums.asp.net/p/975883/3646110.aspx#1291641
                        img.Save(memoryStream, ImageFormat.Png);
                        buffer = memoryStream.ToArray();

                        // write response
                        context.Response.OutputStream.Write(buffer, 0, buffer.Length);

                        // add to cache
                        context.Cache[cacheKey] = buffer;
                    }
                }
            }
        }

        // demonstrates the use of dynamic styles (themes) for vector layers
        private VectorStyle GetPopDensStyle(Map map, FeatureDataRow row)
        {
            // colorize the polygon according to buying power;
            double pop = Convert.ToDouble(row["POP2005"], NumberFormatInfo.InvariantInfo);
            double area = Convert.ToDouble(row["AREA"], NumberFormatInfo.InvariantInfo);

            var fillColor = Color.Gray; // set grey as default (no data)
            if (area > 0)
            {
                // compute a scale [0..1] for the population density
                float scale = (float)(Math.Min(1.0, Math.Sqrt(pop / area) / 70));

                // use the sharpmap ColorBlend for a color gradient green->yellow->red
                fillColor = SharpMap.Rendering.Thematics.ColorBlend.ThreeColors(Color.Green, Color.Yellow, Color.Red).GetColor(scale);
            }

            // make fill color alpha-transparent
            fillColor = Color.FromArgb(180, fillColor.R, fillColor.G, fillColor.B);

            // set the border width depending on the map scale
            var pen = new Pen(Brushes.Black, (int)(50.0 / map.PixelSize)) { LineJoin = LineJoin.Round };

            return new VectorStyle { Outline = pen, EnableOutline = true, Fill = new SolidBrush(fillColor) };
        }

        #endregion

        #region transform

        // set earth radius according PTV, but the radius doesn't matter for tiles
        // you could also use the Bing/Google radius 6378137 or any arbitrary value! 
        public const double EarthRadius = 6371000.0;

        // calculates a mercator bounding box for a tile key
        public static BoundingBox TileToMercatorAtZoom(int tileX, int tileY, int zoom)
        {
            double earthCircum = EarthRadius * 2.0 * Math.PI;
            double earthHalfCircum = earthCircum / 2;
            double arc = earthCircum / (1 << zoom);

            return new BoundingBox(
                (tileX * arc) - earthHalfCircum, earthHalfCircum - ((tileY + 1) * arc),
                ((tileX + 1) * arc) - earthHalfCircum, earthHalfCircum - (tileY * arc));
        }

        // spherical mercator for SharpMap
        public static ICoordinateTransformation TransformToMercator(ICoordinateSystem source)
        {
            var csFactory = new CoordinateSystemFactory();

            var parameters = new List<ProjectionParameter>
            { 
                new ProjectionParameter("latitude_of_origin", 0), new ProjectionParameter("central_meridian", 0),
                new ProjectionParameter("false_easting", 0), new ProjectionParameter("false_northing", 0),
                new ProjectionParameter("semi_major", EarthRadius), new ProjectionParameter("semi_minor", EarthRadius)
            };

            var projection = csFactory.CreateProjection("Mercator", "Mercator_2SP", parameters);

            var coordSystem = csFactory.CreateProjectedCoordinateSystem(
                "Mercator", source as IGeographicCoordinateSystem, projection, LinearUnit.Metre,
                 new AxisInfo("East", AxisOrientationEnum.East),
                 new AxisInfo("North", AxisOrientationEnum.North));

            return new CoordinateTransformationFactory().CreateFromCoordinateSystems(source, coordSystem);
        }

        #endregion

        #region base implementation

        public bool IsReusable
        {
            get { return true; }
        }

        #endregion
    }
}
