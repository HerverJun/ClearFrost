window.tailwind = window.tailwind || {};
window.tailwind.config = {
    theme: {
        extend: {
            colors: {
                // 经典国风色卡 (Classic Guofeng Palette)
                'porcelain': { // 宣纸色 (背景 - 温润牙色)
                    50: '#fdfbf7',
                    100: '#fcfcf0',
                    200: '#faf7e8', // 浅牙色
                    800: '#5c4b37', // 古墨棕
                },
                'celadon': { // 黛蓝 & 汝窑 (主色 - 沉稳雅致)
                    50: '#f0f4f5',
                    100: '#e1e9eb',
                    200: '#c3d3d6',
                    300: '#a5bdc2',
                    400: '#6a9199',
                    500: '#2c5a71', // 核心主色 - 黛蓝
                    600: '#264d61',
                    700: '#1f3f4f',
                    800: '#18313e',
                    900: '#11222b',
                },
                'ink': { // 松烟墨 (文字 - 纯正水墨)
                    50: '#f8fafc',
                    100: '#f1f5f9',
                    200: '#e2e8f0',
                    300: '#cbd5e1',
                    400: '#94a3b8',
                    500: '#64748b',
                    600: '#475569',
                    700: '#334155',
                    800: '#1a1d21', // 浓墨
                    900: '#111317', // 焦墨
                    950: '#000000',
                },
                'rouge': { // 胭脂 (点缀 - 霁红)
                    50: '#fff1f2',
                    100: '#ffe4e6',
                    400: '#cd5c5c',
                    500: '#9e2a2b', // 霁红
                    600: '#8b2323',
                    700: '#751d1d',
                    800: '#5e1717',
                },
                'bamboo': { // 竹青 (自然墨绿)
                    50: '#f1f5f1',
                    100: '#e3eae3',
                    400: '#6d8b6d',
                    500: '#4f6d3d', // 苍竹
                    600: '#3d542f',
                    700: '#2b3b21',
                    66: '#2b3b21', // Fix for typo in original? Kept 66 as 700 to match original
                },
            },
            fontFamily: {
                serif: ['"Noto Serif SC"', '"Source Han Serif CN"', '"Microsoft YaHei"', 'serif'],
                sans: ['"Inter"', '"Noto Sans SC"', '"Microsoft YaHei"', 'sans-serif'],
                mono: ['"JetBrains Mono"', 'Consolas', 'monospace'],
            },
            boxShadow: {
                'soft': '0 8px 30px -4px rgba(44, 90, 113, 0.15)', // 黛蓝微光
                'float': '0 20px 50px -12px rgba(44, 90, 113, 0.25)',
                'glass': '0 8px 32px 0 rgba(26, 29, 33, 0.05), inset 0 0 0 1px rgba(255, 255, 255, 0.4)',
                'glow': '0 0 15px rgba(44, 90, 113, 0.3), inset 0 0 10px rgba(44, 90, 113, 0.05)',
                'inner-glow': 'inset 0 0 30px rgba(255, 255, 255, 0.5)',
            },
            backgroundImage: {
                // 宣纸质感渐变
                'frost-gradient': 'linear-gradient(135deg, #fdfbf7 0%, #fcfaf2 100%)',
                'card-gradient': 'linear-gradient(180deg, rgba(253, 251, 247, 0.95) 0%, rgba(252, 250, 242, 0.9) 100%)',
                'shimmer': 'linear-gradient(45deg, rgba(165, 189, 194, 0.1) 25%, transparent 25%, transparent 50%, rgba(165, 189, 194, 0.1) 50%, rgba(165, 189, 194, 0.1) 75%, transparent 75%, transparent)',
            }
        }
    }
};
