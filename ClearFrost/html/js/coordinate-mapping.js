// ==========================================
// ClearFrost image/overlay coordinate mapping
// ==========================================
(function () {
    "use strict";

    const root = typeof window !== "undefined" ? window : globalThis;

    function positiveNumber(value, fallback = 0) {
        const number = Number(value);
        return Number.isFinite(number) && number > 0 ? number : fallback;
    }

    function calculateContainedRect(outerWidth, outerHeight, innerWidth, innerHeight) {
        outerWidth = positiveNumber(outerWidth);
        outerHeight = positiveNumber(outerHeight);
        innerWidth = positiveNumber(innerWidth);
        innerHeight = positiveNumber(innerHeight);
        if (!outerWidth || !outerHeight || !innerWidth || !innerHeight) {
            return { x: 0, y: 0, width: 0, height: 0, scale: 0 };
        }

        const scale = Math.min(outerWidth / innerWidth, outerHeight / innerHeight);
        const width = innerWidth * scale;
        const height = innerHeight * scale;
        return {
            x: (outerWidth - width) / 2,
            y: (outerHeight - height) / 2,
            width,
            height,
            scale,
        };
    }

    function calculateImageContentMapping(input = {}) {
        const containerWidth = positiveNumber(input.containerWidth);
        const containerHeight = positiveNumber(input.containerHeight);
        const previewWidth = positiveNumber(input.previewWidth, positiveNumber(input.naturalWidth));
        const previewHeight = positiveNumber(input.previewHeight, positiveNumber(input.naturalHeight));
        const sourceWidth = positiveNumber(input.sourceWidth, previewWidth);
        const sourceHeight = positiveNumber(input.sourceHeight, previewHeight);

        if (!containerWidth || !containerHeight || !previewWidth || !previewHeight || !sourceWidth || !sourceHeight) {
            return {
                valid: false,
                sourceWidth,
                sourceHeight,
                previewRect: { x: 0, y: 0, width: 0, height: 0 },
                imageRect: { x: 0, y: 0, width: 0, height: 0 },
                scaleX: 0,
                scaleY: 0,
            };
        }

        const previewRect = calculateContainedRect(containerWidth, containerHeight, previewWidth, previewHeight);
        const sourceInPreviewRect = calculateContainedRect(previewWidth, previewHeight, sourceWidth, sourceHeight);
        const previewScaleX = previewRect.width / previewWidth;
        const previewScaleY = previewRect.height / previewHeight;
        const imageRect = {
            x: previewRect.x + sourceInPreviewRect.x * previewScaleX,
            y: previewRect.y + sourceInPreviewRect.y * previewScaleY,
            width: sourceInPreviewRect.width * previewScaleX,
            height: sourceInPreviewRect.height * previewScaleY,
        };

        return {
            valid: imageRect.width > 0 && imageRect.height > 0,
            sourceWidth,
            sourceHeight,
            previewWidth,
            previewHeight,
            previewRect,
            sourceInPreviewRect,
            imageRect,
            scaleX: imageRect.width / sourceWidth,
            scaleY: imageRect.height / sourceHeight,
        };
    }

    function mapImagePoint(mapping, point = {}) {
        const x = Number(point.x ?? point.X ?? 0);
        const y = Number(point.y ?? point.Y ?? 0);
        return {
            x: x * mapping.scaleX,
            y: y * mapping.scaleY,
        };
    }

    function mapImageRect(mapping, rect = {}) {
        const x = Number(rect.x ?? rect.X ?? 0);
        const y = Number(rect.y ?? rect.Y ?? 0);
        const width = Number(rect.width ?? rect.Width ?? rect.w ?? rect.W ?? 0);
        const height = Number(rect.height ?? rect.Height ?? rect.h ?? rect.H ?? 0);
        return {
            x: x * mapping.scaleX,
            y: y * mapping.scaleY,
            width: width * mapping.scaleX,
            height: height * mapping.scaleY,
        };
    }

    const CoordinateMappingTestCases = Object.freeze([
        {
            name: "16:9 source without bars",
            input: { containerWidth: 1200, containerHeight: 675, previewWidth: 960, previewHeight: 540, sourceWidth: 1280, sourceHeight: 720 },
            rect: { x: 0, y: 0, width: 1280, height: 720 },
            expectedRect: { x: 0, y: 0, width: 1200, height: 675 },
        },
        {
            name: "4:3 source in 16:9 backend preview",
            input: { containerWidth: 960, containerHeight: 540, previewWidth: 960, previewHeight: 540, sourceWidth: 1024, sourceHeight: 768 },
            rect: { x: 0, y: 0, width: 1024, height: 768 },
            expectedImageRect: { x: 120, y: 0, width: 720, height: 540 },
            expectedRect: { x: 0, y: 0, width: 720, height: 540 },
        },
        {
            name: "portrait source in scaled preview",
            input: { containerWidth: 480, containerHeight: 270, previewWidth: 960, previewHeight: 540, sourceWidth: 720, sourceHeight: 1280 },
            rect: { x: 0, y: 0, width: 720, height: 1280 },
            expectedImageRect: { x: 164.0625, y: 0, width: 151.875, height: 270 },
            expectedRect: { x: 0, y: 0, width: 151.875, height: 270 },
        },
        {
            name: "wide source with browser object-fit bars",
            input: { containerWidth: 1000, containerHeight: 800, previewWidth: 960, previewHeight: 540, sourceWidth: 2000, sourceHeight: 500 },
            rect: { x: 0, y: 0, width: 2000, height: 500 },
            expectedImageRect: { x: 0, y: 275, width: 1000, height: 250 },
            expectedRect: { x: 0, y: 0, width: 1000, height: 250 },
        },
    ]);

    function assertClose(actual, expected, label) {
        if (Math.abs(actual - expected) > 0.001) {
            throw new Error(`${label}: expected ${expected}, got ${actual}`);
        }
    }

    function runCoordinateMappingSelfTests() {
        CoordinateMappingTestCases.forEach((testCase) => {
            const mapping = calculateImageContentMapping(testCase.input);
            if (!mapping.valid) throw new Error(`${testCase.name}: mapping invalid`);
            if (testCase.expectedImageRect) {
                assertClose(mapping.imageRect.x, testCase.expectedImageRect.x, `${testCase.name} imageRect.x`);
                assertClose(mapping.imageRect.y, testCase.expectedImageRect.y, `${testCase.name} imageRect.y`);
                assertClose(mapping.imageRect.width, testCase.expectedImageRect.width, `${testCase.name} imageRect.width`);
                assertClose(mapping.imageRect.height, testCase.expectedImageRect.height, `${testCase.name} imageRect.height`);
            }

            const mappedRect = mapImageRect(mapping, testCase.rect);
            assertClose(mappedRect.x, testCase.expectedRect.x, `${testCase.name} rect.x`);
            assertClose(mappedRect.y, testCase.expectedRect.y, `${testCase.name} rect.y`);
            assertClose(mappedRect.width, testCase.expectedRect.width, `${testCase.name} rect.width`);
            assertClose(mappedRect.height, testCase.expectedRect.height, `${testCase.name} rect.height`);
        });
        return { ok: true, count: CoordinateMappingTestCases.length };
    }

    const api = {
        CoordinateMappingTestCases,
        calculateContainedRect,
        calculateImageContentMapping,
        mapImagePoint,
        mapImageRect,
        runCoordinateMappingSelfTests,
    };

    root.CF_COORDINATE_MAPPING = api;
    if (typeof module !== "undefined" && module.exports) {
        module.exports = api;
    }
})();
