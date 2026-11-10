import { request } from "./util.js"
import { map_song_list } from "./util.js"
import { buildUrlPayload } from "../../quality.js"

const QUALITY_MAP = {
    '128': { level: 'standard', encodeType: 'mp3' },
    'standard': { level: 'standard', encodeType: 'mp3' },
    'higher': { level: 'higher', encodeType: 'mp3' },
    '320': { level: 'exhigh', encodeType: 'mp3' },
    'exhigh': { level: 'exhigh', encodeType: 'mp3' },
    'flac': { level: 'lossless', encodeType: 'flac' },
    'lossless': { level: 'lossless', encodeType: 'flac' },
    'hires': { level: 'hires', encodeType: 'flac' },
    'jyeffect': { level: 'jyeffect', encodeType: 'flac' },
    'sky': { level: 'sky', encodeType: 'flac' },
    'jymaster': { level: 'jymaster', encodeType: 'flac' },
    'dolby': { level: 'dolby', encodeType: 'flac' },
}

/** 接口返回的 level → 对外 quality 键（档位对齐网易官方） */
const LEVEL_TO_KEY = {
    standard: 'standard',
    higher: 'higher',
    exhigh: 'exhigh',
    lossless: 'lossless',
    hires: 'hires',
    jyeffect: 'jyeffect',
    sky: 'sky',
    jymaster: 'jymaster',
    dolby: 'dolby',
}

/** 由高到低：杜比 → 母带 → 沉浸 → 臻音 → Hi-Res → 无损 → … */
const FALLBACK_CHAIN = [
    'dolby',
    'jymaster',
    'sky',
    'jyeffect',
    'hires',
    'lossless',
    'exhigh',
    'higher',
    'standard',
]

const resolveQuality = (quality) => {
    if (!quality) return { level: 'standard', encodeType: 'flac', key: 'standard' }
    const key = quality.toLowerCase()
    const mapped = QUALITY_MAP[key]
    if (!mapped) return { level: 'standard', encodeType: 'flac', key: 'standard' }
    return { ...mapped, key }
}

const buildFallbackLevels = (requestedLevel) => {
    const idx = FALLBACK_CHAIN.indexOf(requestedLevel)
    if (idx === -1) return [requestedLevel, 'standard']
    return FALLBACK_CHAIN.slice(idx)
}

const levelRank = (level) => {
    const idx = FALLBACK_CHAIN.indexOf(level)
    return idx === -1 ? FALLBACK_CHAIN.length : idx
}

/** 高清臻音等档：eapi 默认 os=android 会被静默降到无损，需伪装 pc/iphone */
/** 高清臻音等档：eapi 默认 os=android 会被静默降到无损，需伪装 pc/iphone */
const PREMIUM_LEVELS = new Set(['hires', 'jyeffect', 'sky', 'jymaster', 'dolby'])

const withClientOs = (cookie, level) => {
    if (!PREMIUM_LEVELS.has(level)) return cookie || {}
    if (typeof cookie === 'string') {
        const parts = cookie
            .split(';')
            .map((s) => s.trim())
            .filter(Boolean)
            .filter((p) => !/^os=/i.test(p))
        parts.push('os=pc')
        if (!parts.some((p) => /^appver=/i.test(p))) {
            parts.push('appver=3.0.18.203152')
        }
        return parts.join('; ')
    }
    return {
        ...(cookie || {}),
        os: 'pc',
        appver: cookie?.appver || '3.0.18.203152',
    }
}

const fetchUrlByLevel = async (id, cookie, level, encodeType) => {
    const data = {
        ids: '[' + id + ']',
        level,
        encodeType,
    }

    if (level === 'sky') {
        data.immerseType = 'c51'
    }

    const res = await request(
        'POST',
        `https://interface.music.163.com/eapi/song/enhance/player/url/v1`,
        data,
        {
            crypto: 'eapi',
            url: '/api/song/enhance/player/url/v1',
            cookie: withClientOs(cookie, level),
        },
    )

    const item = res.data?.[0]
    const url = item?.url?.replace('http://', 'https://') || ''
    if (!url) return null

    // 网易云常对高音质「静默降级」：仍返回 url，但 level 写真实档位
    const actualLevel = item.level || level
    return {
        url,
        level: actualLevel,
        br: item.br,
        size: item.size,
        type: item.type,
        duration: item.time,
        previewOnly: Boolean(item.freeTrialInfo),
        loudness: {
            gain: item.gain,
            peak: item.peak,
            closedGain: item.closedGain,
            closedPeak: item.closedPeak,
        },
    }
}

export const get_song_url = async (id, cookie = '', options = {}) => {
    const requested = resolveQuality(options.quality)
    const levels = buildFallbackLevels(requested.level)
    // 已试过的真实档位，避免 jymaster→返回 lossless 后再重复请求 lossless
    const seenActual = new Set()

    for (const tryLevel of levels) {
        if (seenActual.has(tryLevel)) continue
        try {
            const encode = QUALITY_MAP[tryLevel]?.encodeType
                || (tryLevel === 'standard' || tryLevel === 'higher' || tryLevel === 'exhigh' ? 'mp3' : 'flac')
            const result = await fetchUrlByLevel(id, cookie, tryLevel, encode)
            if (!result?.url) continue

            const actualLevel = result.level
            seenActual.add(actualLevel)

            // 若返回档位比请求档位更低，视为平台静默降级，以真实 level 标注
            if (actualLevel !== tryLevel) {
                console.log(`[netease] requested ${tryLevel} but API returned ${actualLevel} for ${id}`)
            }

            // 只接受「不高于请求档」的结果（防止异常抬高）；rank 越小越高
            if (levelRank(actualLevel) < levelRank(requested.level)) {
                // 实际比请求还高（少见），仍按实际返回
            }

            const qualityKey = LEVEL_TO_KEY[actualLevel] || actualLevel
            if (qualityKey !== requested.key) {
                console.log(`[netease] quality ${requested.key} unavailable for ${id}, got ${qualityKey}`)
            }
            return Object.assign(buildUrlPayload(
                result.url,
                qualityKey,
                requested.key,
                'netease',
                result.loudness,
                result.duration,
            ), { previewOnly: result.previewOnly })
        } catch (e) {
            console.error(`[netease] fetch url level=${tryLevel} failed:`, e?.message || e)
        }
    }

    return null
}

export const get_song_info = async (id, cookie = '') => {
    const ids = [id]
    const data = {
        c: '[' + ids.map((id) => '{"id":' + id + '}').join(',') + ']',
    }
    let res = await request('POST', `https://music.163.com/api/v3/song/detail`, data, {
        crypto: 'weapi',
        cookie: cookie || {}
    })

    if (!res.songs) {
        throw res
    }

    res = map_song_list(res)
    return res
}
