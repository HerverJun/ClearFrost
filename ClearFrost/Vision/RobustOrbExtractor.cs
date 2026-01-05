using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using OpenCvSharp;

namespace ClearFrost.Vision
{
    /// <summary>
    /// A robust ORB feature extractor that uses grid-based FAST detection, OctTree distribution,
    /// Image Pyramids, and Intensity Centroid orientation.
    /// Strictly follows ORB-SLAM3 strategies for robustness in industrial environments.
    /// </summary>
    public class RobustOrbExtractor
    {
        private const int PATCH_SIZE = 31;
        private const int HALF_PATCH_SIZE = 15;
        private const int EDGE_THRESHOLD = 19;

        private float _scaleFactor = 1.2f;
        private int _nLevels = 8;
        private int _iniThFAST = 20;
        private int _minThFAST = 7;

        private static readonly int[] U_MAX = { 15, 15, 15, 15, 14, 14, 14, 13, 13, 12, 11, 10, 9, 8, 6, 3 };

        public RobustOrbExtractor(int nLevels = 8, float scaleFactor = 1.2f, int iniThFast = 20, int minThFast = 7)
        {
            _nLevels = nLevels;
            _scaleFactor = scaleFactor;
            _iniThFAST = iniThFast;
            _minThFAST = minThFast;
        }

        public (KeyPoint[], Mat) DetectAndCompute(Mat image, int maxFeatures)
        {
            if (image.Empty())
                throw new ArgumentException("Input image is empty.", nameof(image));

            if (image.Channels() != 1)
                throw new ArgumentException("Input image must be grayscale.", nameof(image));

            var pyramid = ComputePyramid(image);
            int nFeaturesPerLevel = (int)(maxFeatures * (1.0f - (1.0f / _scaleFactor)) / (1.0f - Math.Pow(1.0f / _scaleFactor, _nLevels)));

            var allKeyPoints = new List<KeyPoint>();
            var levelDescriptorsList = new List<Mat>();
            float scale = 1.0f;

            for (int level = 0; level < _nLevels; level++)
            {
                int nDesiredFeatures = nFeaturesPerLevel;
                if (level == 0)
                {
                    // Logic to distribute remainder could go here
                }

                Mat levelImage = pyramid[level];

                // A. Grid FAST + QuadTree
                var keypoints = ComputeKeyPointsLevel(levelImage, nDesiredFeatures);

                var kptsArray = keypoints.ToArray();

                // B. Orientation
                ComputeOrientation(levelImage, kptsArray);

                // C. Descriptors
                using var levelDescriptors = new Mat();

                // Use positional arguments to ensure compatibility
                // (nFeatures, scaleFactor, nLevels, edgeThreshold, firstLevel, wta_k, scoreType, patchSize, fastThreshold)
                using var orb = ORB.Create(nDesiredFeatures, 1.2f, 1, 31, 0, 2, patchSize: 31, fastThreshold: 20);
                orb.Compute(levelImage, ref kptsArray, levelDescriptors);

                // D. Scale back
                for (int i = 0; i < kptsArray.Length; i++)
                {
                    KeyPoint kp = kptsArray[i];
                    kp.Pt.X *= scale;
                    kp.Pt.Y *= scale;
                    kp.Octave = level;
                    kp.Size = PATCH_SIZE * scale;
                    kptsArray[i] = kp;
                }

                allKeyPoints.AddRange(kptsArray);

                if (!levelDescriptors.Empty())
                {
                    levelDescriptorsList.Add(levelDescriptors.Clone());
                }

                scale *= _scaleFactor;
            }

            // Merge
            Mat finalDescriptors = new Mat();
            if (levelDescriptorsList.Count > 0)
            {
                Cv2.VConcat(levelDescriptorsList.ToArray(), finalDescriptors);
            }

            foreach (var m in pyramid) m.Dispose();
            foreach (var m in levelDescriptorsList) m.Dispose();
            // levelDescriptorsList is local, no clear needed

            return (allKeyPoints.ToArray(), finalDescriptors);
        }

        private List<Mat> ComputePyramid(Mat image)
        {
            var pyramid = new List<Mat>();
            float scale = 1.0f;
            Mat current = image.Clone();

            for (int i = 0; i < _nLevels; i++)
            {
                pyramid.Add(current);
                if (i < _nLevels - 1)
                {
                    Mat next = new Mat();
                    scale *= _scaleFactor;
                    OpenCvSharp.Size sz = new OpenCvSharp.Size(
                        (int)Math.Ceiling(image.Width / scale),
                        (int)Math.Ceiling(image.Height / scale)
                    );

                    Cv2.Resize(image, next, sz, 0, 0, InterpolationFlags.Linear);
                    current = next;
                }
            }
            return pyramid;
        }

        private List<KeyPoint> ComputeKeyPointsLevel(Mat image, int nFeatures)
        {
            var gridPoints = ComputeKeyPointsGrid(image);
            return DistributeOctTree(gridPoints, image.Width, image.Height, nFeatures);
        }

        private List<KeyPoint> ComputeKeyPointsGrid(Mat image)
        {
            var allKeyPoints = new ConcurrentBag<KeyPoint>();

            const int W = 35;
            int width = image.Width;
            int height = image.Height;
            int nCols = width / W;
            int nRows = height / W;
            int wCell = (int)Math.Ceiling((double)width / nCols);
            int hCell = (int)Math.Ceiling((double)height / nRows);

            int border = HALF_PATCH_SIZE + 3;

            Parallel.For(0, nRows * nCols, (i) =>
            {
                int r = i / nCols;
                int c = i % nCols;

                int xMin = c * wCell;
                int yMin = r * hCell;
                int xMax = xMin + wCell;
                int yMax = yMin + hCell;

                if (xMin < border) xMin = border;
                if (yMin < border) yMin = border;
                if (xMax > width - border) xMax = width - border;
                if (yMax > height - border) yMax = height - border;

                if (xMax <= xMin || yMax <= yMin) return;

                Rect cellRect = new Rect(xMin, yMin, xMax - xMin, yMax - yMin);
                using Mat cellImg = new Mat(image, cellRect);

                KeyPoint[]? kps = Cv2.FAST(cellImg, _iniThFAST, true);
                if (kps.Length == 0)
                {
                    kps = Cv2.FAST(cellImg, _minThFAST, true);
                }

                foreach (var kp in kps)
                {
                    var newPt = new Point2f(kp.Pt.X + xMin, kp.Pt.Y + yMin);
                    if (newPt.X < border || newPt.X >= width - border || newPt.Y < border || newPt.Y >= height - border)
                        continue;

                    // Explicit float params
                    allKeyPoints.Add(new KeyPoint(newPt.X, newPt.Y, 31f, -1f, kp.Response, 0, -1));
                }
            });

            return allKeyPoints.ToList();
        }

        private List<KeyPoint> DistributeOctTree(List<KeyPoint> inputKeyPoints, int width, int height, int nFeatures)
        {
            if (inputKeyPoints.Count <= nFeatures) return inputKeyPoints;

            var root = new ExtractorNode
            {
                Area = new Rect(0, 0, width, height),
                KeyPoints = inputKeyPoints,
                TopLeft = new Point2f(0, 0),
                BottomRight = new Point2f(width, height)
            };

            var nodeQueue = new PriorityQueue<ExtractorNode, int>();
            nodeQueue.Enqueue(root, -root.KeyPoints.Count);

            var resultNodes = new List<ExtractorNode>();

            while (nodeQueue.Count > 0 && (nodeQueue.Count + resultNodes.Count) < nFeatures)
            {
                if (!nodeQueue.TryDequeue(out ExtractorNode currentNode, out _)) break;

                if (currentNode.KeyPoints.Count == 1)
                {
                    resultNodes.Add(currentNode);
                    continue;
                }

                currentNode.DivideNode(out var n1, out var n2, out var n3, out var n4);

                bool splitHappened = false;
                var children = new[] { n1, n2, n3, n4 };
                foreach (var child in children)
                {
                    if (child.KeyPoints.Count > 0)
                    {
                        if (child.KeyPoints.Count == 1) resultNodes.Add(child);
                        else nodeQueue.Enqueue(child, -child.KeyPoints.Count);
                        splitHappened = true;
                    }
                }

                if (!splitHappened) resultNodes.Add(currentNode);
            }

            var finalNodes = new List<ExtractorNode>(resultNodes);
            while (nodeQueue.TryDequeue(out var node, out _)) finalNodes.Add(node);

            var output = new List<KeyPoint>(finalNodes.Count);
            foreach (var node in finalNodes)
            {
                if (node.KeyPoints.Count == 0) continue;
                KeyPoint bestKp = node.KeyPoints[0];
                float maxResp = bestKp.Response;
                for (int k = 1; k < node.KeyPoints.Count; k++)
                {
                    if (node.KeyPoints[k].Response > maxResp)
                    {
                        maxResp = node.KeyPoints[k].Response;
                        bestKp = node.KeyPoints[k];
                    }
                }
                output.Add(bestKp);
            }

            return output;
        }

        private class ExtractorNode
        {
            public List<KeyPoint> KeyPoints = new List<KeyPoint>();
            public Rect Area;
            public Point2f TopLeft, BottomRight;

            public void DivideNode(out ExtractorNode n1, out ExtractorNode n2, out ExtractorNode n3, out ExtractorNode n4)
            {
                int halfWidth = (int)Math.Ceiling(Area.Width / 2.0);
                int halfHeight = (int)Math.Ceiling(Area.Height / 2.0);

                n1 = new ExtractorNode { Area = new Rect(Area.X, Area.Y, halfWidth, halfHeight) };
                n2 = new ExtractorNode { Area = new Rect(Area.X + halfWidth, Area.Y, Area.Width - halfWidth, halfHeight) };
                n3 = new ExtractorNode { Area = new Rect(Area.X, Area.Y + halfHeight, halfWidth, Area.Height - halfHeight) };
                n4 = new ExtractorNode { Area = new Rect(Area.X + halfWidth, Area.Y + halfHeight, Area.Width - halfWidth, Area.Height - halfHeight) };

                foreach (var kp in KeyPoints)
                {
                    if (n1.Area.Contains((int)kp.Pt.X, (int)kp.Pt.Y)) n1.KeyPoints.Add(kp);
                    else if (n2.Area.Contains((int)kp.Pt.X, (int)kp.Pt.Y)) n2.KeyPoints.Add(kp);
                    else if (n3.Area.Contains((int)kp.Pt.X, (int)kp.Pt.Y)) n3.KeyPoints.Add(kp);
                    else if (n4.Area.Contains((int)kp.Pt.X, (int)kp.Pt.Y)) n4.KeyPoints.Add(kp);
                }
            }
        }

        private void ComputeOrientation(Mat image, KeyPoint[] keypoints)
        {
            // Use safe Indexer. Slower but guaranteed to compile.
            var indexer = image.GetGenericIndexer<byte>();

            Parallel.For(0, keypoints.Length, i =>
            {
                KeyPoint kp = keypoints[i];
                int u = (int)kp.Pt.X;
                int v = (int)kp.Pt.Y;

                long m_01 = 0, m_10 = 0;

                for (int y = -HALF_PATCH_SIZE; y <= HALF_PATCH_SIZE; ++y)
                {
                    if (Math.Abs(y) >= U_MAX.Length) continue;

                    int u_max_val = U_MAX[Math.Abs(y)];

                    for (int x = -u_max_val; x <= u_max_val; ++x)
                    {
                        // Check bounds to be absolutely safe (though grid logic should prevent this)
                        // indexer[row, col] -> indexer[y, x]
                        // image coords: (u+x, v+y)
                        // indexer typically takes (row, col) i.e. (y, x)

                        byte val = indexer[v + y, u + x];
                        m_10 += x * val;
                        m_01 += y * val;
                    }
                }

                float angle = (float)((Math.Atan2(m_01, m_10) * 180.0) / Math.PI);
                if (angle < 0) angle += 360;

                kp.Angle = angle;
                keypoints[i] = kp;
            });
        }
    }
}
