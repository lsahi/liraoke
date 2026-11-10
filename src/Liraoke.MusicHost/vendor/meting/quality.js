export const VALID_QUALITY = {
    tencent: [
        '128', 'standard', '320', 'exhigh', 'flac', 'lossless',
        'atmos', 'master',
    ],
    netease: [
        '128', 'standard', 'higher', '320', 'exhigh', 'flac', 'lossless', 'hires',
        'jyeffect', 'sky', 'jymaster', 'dolby',
    ],
    qishui: [
        '128', 'standard', '320', 'exhigh', 'studio', 'atmos', 'flac', 'lossless',
    ],
    kugou: [
        '128', 'standard', '320', 'exhigh', 'flac', 'lossless', 'hires', 'atmos', 'master',
        'viper_atmos', 'viper_tape', 'viper_clear', 'viper_hifi', 'acappella', 'multitrack',
    ],
}

/**
 * 音质中文名与会员要求（按平台）
 * 网易：320 / 无损 / Hi-Res / 臻音 → VIP；sky / jymaster / dolby → SVIP
 * QQ：HQ / SQ → VIP；臻品全景声 / 臻品母带 → 超级会员（文档记作超会 / SVIP）
 * 汽水：320 → VIP；无损 / 录音室 / 全景 → SVIP（不支持 higher）`n * 酷狗：320 / 无损 → VIP；Hi-Res / 全景声 / 母带 → SVIP
 */
export const QUALITY_META = {
    '128': { name: '标准', tencent: '标准品质', vip: false, svip: false },
    'standard': { name: '标准', tencent: '标准品质', vip: false, svip: false },
    'higher': { name: '较高', vip: false, svip: false },
    '320': { name: '极高', tencent: 'HQ高品质', vip: true, svip: false },
    'exhigh': { name: '极高', tencent: 'HQ高品质', vip: true, svip: false },
    'flac': { name: '无损', tencent: 'SQ无损品质', vip: true, svip: false, qishuiSvip: true },
    'lossless': { name: '无损', tencent: 'SQ无损品质', vip: true, svip: false, qishuiSvip: true },
    // 网易云
    'hires': { name: '高解析度无损', kugou: 'Hi-Res音质', vip: true, svip: false },
    'viper_atmos': { name: '蝰蛇全景声2.0', kugou: '蝰蛇全景声2.0', vip: false, svip: true },
    'viper_tape': { name: '蝰蛇母带音质', kugou: '蝰蛇母带音质', vip: false, svip: true },
    'viper_clear': { name: '蝰蛇超清音质', kugou: '蝰蛇超清音质', vip: false, svip: true },
    'viper_hifi': { name: '蝰蛇HiFi音质', kugou: '蝰蛇HiFi音质', vip: false, svip: true },
    'acappella': { name: '人声伴奏', kugou: '人声伴奏', vip: false, svip: true },
    'multitrack': { name: '多轨音质', kugou: '多轨音质', vip: false, svip: true },
    'jyeffect': { name: '高清臻音', vip: true, svip: false },
    'sky': { name: '沉浸环绕声', vip: false, svip: true },
    'jymaster': { name: '超清母带', vip: false, svip: true },
    'dolby': { name: '杜比全景声', vip: false, svip: true },
    // QQ 音乐（超级会员）/ 汽水全景
    'atmos': { name: '臻品全景声', qishui: '全景声', tencent: '臻品全景声', kugou: '蝰蛇全景声2.0', vip: false, svip: true, qishuiSvip: true },
    'master': { name: '臻品母带', tencent: '臻品母带', kugou: '母带', vip: false, svip: true },
    // 汽水音乐
    'studio': { name: '录音室音质', qishui: '录音室音质', vip: false, svip: true, qishuiSvip: true },
}

export const isValidQuality = (server, quality) => {
    if (!quality) return true
    return VALID_QUALITY[server]?.includes(quality.toLowerCase()) ?? false
}

export const getQualityRequirement = (quality, server) => {
    const key = quality?.toLowerCase()
    const meta = QUALITY_META[key]
    if (!meta) return '免费'
    if (server === 'qishui' && meta.qishuiSvip) return 'SVIP'
    if (meta.svip) return 'SVIP'
    if (meta.vip) return 'VIP'
    return '免费'
}

export const getQualityName = (quality, server) => {
    if (!quality) return server === 'tencent' ? '标准品质' : '标准'
    const meta = QUALITY_META[quality.toLowerCase()]
    if (!meta) return quality
    if (server === 'tencent' && meta.tencent) return meta.tencent
    if (server === 'qishui' && meta.qishui) return meta.qishui
    if (server === 'kugou' && meta.kugou) return meta.kugou
    return meta.name
}

const finiteNumber = (value) => {
    if (value === null || value === undefined || value === '' || typeof value === 'boolean') return undefined
    const number = Number(value)
    return Number.isFinite(number) ? number : undefined
}

export const normalizeDuration = (value) => {
    const duration = finiteNumber(value)
    if (duration === undefined || duration <= 0) return undefined
    return Math.round(duration)
}

export const normalizeLoudness = (raw) => {
    if (!raw || typeof raw !== 'object') return undefined
    const loudness = {}
    for (const field of ['gain', 'peak', 'lra', 'closedGain', 'closedPeak']) {
        const value = finiteNumber(raw[field])
        if (value !== undefined) loudness[field] = value
    }
    return Object.keys(loudness).length ? loudness : undefined
}

/** 统一 type=url 的返回结构；不支持响度的平台明确返回 loudness: null。 */
export const buildUrlPayload = (url, actualQuality, requestedQuality, server, rawLoudness, rawDuration) => {
    if (!url) return null
    const quality = (actualQuality || requestedQuality || 'standard').toLowerCase()
    const payload = {
        url,
        quality: getQualityName(quality, server),
        loudness: normalizeLoudness(rawLoudness) || null,
    }
    const duration = normalizeDuration(rawDuration)
    if (duration) payload.duration = duration
    return payload
}
