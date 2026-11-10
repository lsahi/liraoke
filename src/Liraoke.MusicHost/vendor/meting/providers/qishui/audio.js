import crypto from 'crypto'
import { Buffer } from 'node:buffer'
import { get_public_base } from '../../util.js'

const WEB_UA = 'LunaPC/3.3.0(359450208)'
const MAX_SOURCE_BYTES = 128 * 1024 * 1024

const validateQishuiAudioUrl = (url) => {
    const target = new URL(url)
    if (typeof url !== 'string' || url.length > 8192 || target.protocol !== 'https:'
        || target.username || target.password || target.port || target.hash
        || !target.hostname.endsWith('.douyinvod.com')) {
        throw new Error('汽水音频地址不受支持')
    }
}

const emptyBox = () => ({ size: 0, offset: 0, data: Buffer.alloc(0) })

const findBox = (buffer, wanted, start = 0, end = buffer.length) => {
    let offset = start
    while (offset + 8 <= end) {
        const size = buffer.readUInt32BE(offset)
        if (size < 8 || offset + size > end) break
        const type = buffer.subarray(offset + 4, offset + 8).toString('ascii')
        if (type === wanted) return { size, offset, data: buffer.subarray(offset + 8, offset + size) }
        offset += size
    }
    return emptyBox()
}

const bitCount = (input) => {
    let value = input
    value -= (value >> 1) & 0x55555555
    value = (value & 0x33333333) + ((value >> 2) & 0x33333333)
    return (((value + (value >> 4)) & 0x0f0f0f0f) * 0x01010101) >> 24
}

const decodeSpadeA = (value) => {
    const source = Buffer.from(String(value || ''), 'base64')
    if (source.length < 3) return ''
    const padding = (source[0] ^ source[1] ^ source[2]) - 48
    if (padding < 0 || source.length < padding + 2) return ''
    const input = source.subarray(1, source.length - padding)
    const working = Buffer.alloc(input.length + 2)
    working[0] = 0xfa
    working[1] = 0x55
    input.copy(working, 2)
    const decoded = Buffer.alloc(input.length)
    for (let index = 0; index < decoded.length; index += 1) {
        let byte = (input[index] ^ working[index]) - bitCount(index) - 21
        while (byte < 0) byte += 0xff
        decoded[index] = byte & 0xff
    }
    const first = decoded[0]
    const skip = first >= 48 && first <= 57 ? first - 48 : first >= 97 && first <= 122 ? first - 87 : 255
    const end = 1 + source.length - padding - 2 - skip
    return end > 1 && end <= decoded.length ? decoded.subarray(1, end).toString('utf8') : ''
}

const encryptedBoxTypes = new Set(['senc', 'saio', 'saiz', 'sinf', 'schi', 'tenc', 'schm', 'frma'])
const containerBoxTypes = new Set(['moov', 'trak', 'mdia', 'minf', 'stbl', 'stsd'])

const writeUInt32BE = (value) => {
    const output = Buffer.alloc(4)
    output.writeUInt32BE(value)
    return output
}

const parseSampleSizes = (data) => {
    const size = data.readUInt32BE(4)
    const count = data.readUInt32BE(8)
    if (count > 200000) throw new Error('汽水音频样本数量异常')
    if (size) return Array.from({ length: count }, () => size)
    if (12 + count * 4 > data.length) throw new Error('汽水音频 stsz 数据不完整')
    return Array.from({ length: count }, (_, index) => data.readUInt32BE(12 + index * 4))
}

const parseSampleToChunk = (data) => {
    const count = data.readUInt32BE(4)
    if (count > 20000 || 8 + count * 12 > data.length) throw new Error('汽水音频 stsc 数据不完整')
    return Array.from({ length: count }, (_, index) => {
        const offset = 8 + index * 12
        return { firstChunk: data.readUInt32BE(offset), samplesPerChunk: data.readUInt32BE(offset + 4) }
    })
}

const parseIvs = (data) => {
    const count = data.readUInt32BE(4)
    if (count > 200000 || 8 + count * 8 > data.length) throw new Error('汽水音频 senc 数据不完整')
    return Array.from({ length: count }, (_, index) => {
        const iv = Buffer.alloc(16)
        data.copy(iv, 0, 8 + index * 8, 16 + index * 8)
        return iv
    })
}

const flacMetadata = (stsd) => {
    const index = stsd.data.indexOf(Buffer.from('dfLa'))
    if (index < 4) return Buffer.alloc(0)
    const size = stsd.data.readUInt32BE(index - 4)
    const end = Math.min(index - 4 + size, stsd.data.length)
    return stsd.data.subarray(index + 8, end)
}

const samplesPerChunk = (chunk, entries) => {
    for (let index = 0; index < entries.length; index += 1) {
        const current = entries[index]
        const next = entries[index + 1]
        if (chunk >= current.firstChunk && (!next || chunk < next.firstChunk)) return current.samplesPerChunk
    }
    return 0
}

const rebuildChunkOffsets = (sampleSizes, entries, chunkCount, mdatOffset) => {
    const offsets = []
    let sampleIndex = 0
    let offset = mdatOffset
    for (let chunk = 1; chunk <= chunkCount; chunk += 1) {
        offsets.push(offset)
        const count = samplesPerChunk(chunk, entries)
        for (let index = 0; index < count && sampleIndex < sampleSizes.length; index += 1) {
            offset += sampleSizes[sampleIndex]
            sampleIndex += 1
        }
    }
    return offsets
}

const rewriteStco = (data, offsets) => {
    const count = data.readUInt32BE(4)
    if (count > offsets.length) throw new Error('汽水音频 chunk 偏移不足')
    const output = Buffer.alloc(8 + count * 4)
    data.copy(output, 0, 0, 8)
    offsets.slice(0, count).forEach((offset, index) => output.writeUInt32BE(offset, 8 + index * 4))
    return output
}

const cleanBoxChildren = (source, start, end, context) => {
    const parts = []
    let offset = start
    while (offset < end) {
        if (offset + 8 > end) {
            parts.push(source.subarray(offset, end))
            break
        }
        const size = source.readUInt32BE(offset)
        if (size < 8 || offset + size > end) {
            parts.push(source.subarray(offset, end))
            break
        }
        const type = source.subarray(offset + 4, offset + 8).toString('ascii')
        const child = { offset, size, data: source.subarray(offset + 8, offset + size) }
        if (encryptedBoxTypes.has(type)) {
            offset += size
            continue
        }
        if (type === 'enca') {
            // AudioSampleEntry 有 28 字节固定头，真正的子 box 从其后开始。
            const fixedHeaderEnd = Math.min(offset + size, offset + 36)
            const fixedHeader = source.subarray(offset + 8, fixedHeaderEnd)
            const inner = cleanBoxChildren(source, fixedHeaderEnd, offset + size, context)
            parts.push(writeUInt32BE(fixedHeader.length + inner.length + 8), Buffer.from('mp4a'), fixedHeader, inner)
        } else if (type === 'stco') {
            const body = rewriteStco(child.data, rebuildChunkOffsets(
                context.sampleSizes, context.sampleToChunk, context.chunkCount, context.mdatOffset,
            ))
            parts.push(writeUInt32BE(body.length + 8), Buffer.from('stco'), body)
        } else if (containerBoxTypes.has(type)) {
            // stsd 前 8 字节为 FullBox 版本和 entry_count，不是 MP4 子 box。
            const fixedHeaderSize = type === 'stsd' ? 8 : 0
            const fixedHeaderEnd = Math.min(offset + size, offset + 8 + fixedHeaderSize)
            const fixedHeader = source.subarray(offset + 8, fixedHeaderEnd)
            const inner = cleanBoxChildren(source, fixedHeaderEnd, offset + size, context)
            parts.push(writeUInt32BE(fixedHeader.length + inner.length + 8), Buffer.from(type), fixedHeader, inner)
        } else {
            parts.push(source.subarray(offset, offset + size))
        }
        offset += size
    }
    return Buffer.concat(parts)
}

const cleanBoxTree = (source, box, context) => cleanBoxChildren(source, box.offset + 8, box.offset + box.size, context)

export const decryptQishuiAudio = (source, spadeA) => {
    const encrypted = Buffer.isBuffer(source) ? source : Buffer.from(source)
    const keyHex = /^[0-9a-f]+$/i.test(String(spadeA || '')) ? String(spadeA) : decodeSpadeA(spadeA)
    const key = Buffer.from(keyHex, 'hex')
    if (key.length !== 16) throw new Error('汽水音频密钥无效')

    const moov = findBox(encrypted, 'moov')
    const trak = findBox(encrypted, 'trak', moov.offset + 8, moov.offset + moov.size)
    const mdia = findBox(encrypted, 'mdia', trak.offset + 8, trak.offset + trak.size)
    const minf = findBox(encrypted, 'minf', mdia.offset + 8, mdia.offset + mdia.size)
    const stbl = findBox(encrypted, 'stbl', minf.offset + 8, minf.offset + minf.size)
    const stsd = findBox(encrypted, 'stsd', stbl.offset + 8, stbl.offset + stbl.size)
    const stsz = findBox(encrypted, 'stsz', stbl.offset + 8, stbl.offset + stbl.size)
    const stsc = findBox(encrypted, 'stsc', stbl.offset + 8, stbl.offset + stbl.size)
    const stco = findBox(encrypted, 'stco', stbl.offset + 8, stbl.offset + stbl.size)
    let senc = findBox(encrypted, 'senc', moov.offset + 8, moov.offset + moov.size)
    if (!senc.size) senc = findBox(encrypted, 'senc', stbl.offset + 8, stbl.offset + stbl.size)
    const mdat = findBox(encrypted, 'mdat')
    if (![moov, trak, mdia, minf, stbl, stsd, stsz, stsc, stco, senc, mdat].every(box => box.size)) {
        throw new Error('汽水音频容器缺少必要 MP4 box')
    }

    const sizes = parseSampleSizes(stsz.data)
    const sampleToChunk = parseSampleToChunk(stsc.data)
    const chunkCount = stco.data.readUInt32BE(4)
    if (chunkCount > 200000 || 8 + chunkCount * 4 > stco.data.length) throw new Error('汽水音频 stco 数据不完整')
    const sourceChunkOffsets = Array.from({ length: chunkCount }, (_, index) => stco.data.readUInt32BE(8 + index * 4))
    const ivs = parseIvs(senc.data)
    if (ivs.length < sizes.length) throw new Error('汽水音频样本与 IV 数量不一致')
    const samples = []
    let sampleIndex = 0
    for (let chunk = 1; chunk <= chunkCount && sampleIndex < sizes.length; chunk += 1) {
        let offset = sourceChunkOffsets[chunk - 1]
        const count = samplesPerChunk(chunk, sampleToChunk)
        if (!count) throw new Error('汽水音频 stsc 缺少 chunk 映射')
        for (let index = 0; index < count && sampleIndex < sizes.length; index += 1) {
            const size = sizes[sampleIndex]
            if (offset < 0 || offset + size > encrypted.length) throw new Error('汽水音频样本数据不完整')
            const decipher = crypto.createDecipheriv('aes-128-ctr', key, ivs[sampleIndex])
            samples.push(Buffer.concat([decipher.update(encrypted.subarray(offset, offset + size)), decipher.final()]))
            offset += size
            sampleIndex += 1
        }
    }
    if (sampleIndex !== sizes.length) throw new Error('汽水音频样本数量与 chunk 映射不一致')

    const metadata = flacMetadata(stsd)
    if (metadata.length) {
        return { buffer: Buffer.concat([Buffer.from('fLaC'), metadata, ...samples]), contentType: 'audio/flac' }
    }
    const ftyp = findBox(encrypted, 'ftyp')
    // 移除 CENC 描述后 moov 长度会变化，先测量一次，再按新的 mdat 偏移重建 stco。
    const draftMoov = cleanBoxTree(encrypted, moov, { sampleSizes: sizes, sampleToChunk, chunkCount, mdatOffset: 0 })
    const mdatOffset = (ftyp.size || 0) + draftMoov.length + 16
    const cleanMoovData = cleanBoxTree(encrypted, moov, { sampleSizes: sizes, sampleToChunk, chunkCount, mdatOffset })
    const cleanMoov = Buffer.concat([writeUInt32BE(cleanMoovData.length + 8), Buffer.from('moov'), cleanMoovData])
    const cleanMdatData = Buffer.concat(samples)
    const cleanMdat = Buffer.concat([writeUInt32BE(cleanMdatData.length + 8), Buffer.from('mdat'), cleanMdatData])
    return { buffer: Buffer.concat([ftyp.size ? encrypted.subarray(ftyp.offset, ftyp.offset + ftyp.size) : Buffer.alloc(0), cleanMoov, cleanMdat]), contentType: 'audio/mp4' }
}

const cache = new Map()
const inflightLoads = new Map()
const MAX_CACHE_BYTES = 512 * 1024 * 1024
const MAX_DISK_CACHE_BYTES = 512 * 1024 * 1024
const DISK_CACHE_TTL_MS = 6 * 60 * 60 * 1000
let cacheBytes = 0

const diskCacheDir = (() => {
    const runtime = globalThis?.process?.release?.name === 'node' || globalThis?.Bun !== undefined
    if (!runtime) return ''
    const base = String(globalThis?.process?.env?.DATA_DIR || './data').trim() || './data'
    return `${base.replace(/\\/g, '/').replace(/\/+$/, '')}/qishui-audio-cache`
})()

let diskFs = null
let diskPath = null
let diskCleanupPromise = null

const getDiskModules = async () => {
    if (!diskCacheDir) return null
    if (diskFs && diskPath) return { fs: diskFs, path: diskPath }
    diskFs = await import('node:fs/promises')
    diskPath = await import('node:path')
    await diskFs.mkdir(diskCacheDir, { recursive: true })
    void cleanupDiskCache()
    return { fs: diskFs, path: diskPath }
}

const diskPaths = (key) => ({
    body: `${diskCacheDir}/${key}.bin`,
    meta: `${diskCacheDir}/${key}.json`,
})

const cleanupDiskCache = async () => {
    if (!diskFs || !diskCacheDir) return
    if (diskCleanupPromise) return diskCleanupPromise
    diskCleanupPromise = (async () => {
        const entries = []
        try {
            const names = await diskFs.readdir(diskCacheDir)
            for (const name of names) {
                if (!name.endsWith('.json')) continue
                const key = name.slice(0, -5)
                const paths = diskPaths(key)
                try {
                    const meta = JSON.parse(await diskFs.readFile(paths.meta, 'utf8'))
                    const stat = await diskFs.stat(paths.body)
                    entries.push({ key, at: Number(meta.at) || 0, bytes: stat.size })
                } catch {
                    await diskFs.unlink(paths.meta).catch(() => {})
                    await diskFs.unlink(paths.body).catch(() => {})
                }
            }

            const now = Date.now()
            const expired = entries.filter((entry) => !entry.at || now - entry.at > DISK_CACHE_TTL_MS)
            for (const entry of expired) {
                const paths = diskPaths(entry.key)
                await diskFs.unlink(paths.meta).catch(() => {})
                await diskFs.unlink(paths.body).catch(() => {})
            }

            const active = entries
                .filter((entry) => !expired.includes(entry))
                .sort((a, b) => a.at - b.at)
            let total = active.reduce((sum, entry) => sum + entry.bytes, 0)
            for (const entry of active) {
                if (total <= MAX_DISK_CACHE_BYTES) break
                const paths = diskPaths(entry.key)
                await diskFs.unlink(paths.meta).catch(() => {})
                await diskFs.unlink(paths.body).catch(() => {})
                total -= entry.bytes
            }
        } catch (error) {
            console.warn('[QishuiAudio] 磁盘缓存清理失败:', error?.message || error)
        } finally {
            diskCleanupPromise = null
        }
    })()
    return diskCleanupPromise
}

const readDiskCache = async (key) => {
    const mods = await getDiskModules()
    if (!mods) return null
    const { fs } = mods
    const paths = diskPaths(key)
    try {
        const metaRaw = await fs.readFile(paths.meta, 'utf8')
        const meta = JSON.parse(metaRaw)
        const buffer = await fs.readFile(paths.body)
        if (!buffer?.length || !meta?.contentType) return null
        return { buffer, contentType: meta.contentType, loudness: meta.loudness, at: meta.at || Date.now() }
    } catch {
        return null
    }
}

const writeDiskCache = async (key, value) => {
    const mods = await getDiskModules()
    if (!mods) return
    const { fs } = mods
    const paths = diskPaths(key)
    const tmpBody = `${paths.body}.${process.pid}.tmp`
    const tmpMeta = `${paths.meta}.${process.pid}.tmp`
    try {
        await fs.writeFile(tmpBody, value.buffer)
        await fs.writeFile(tmpMeta, JSON.stringify({
            contentType: value.contentType,
            loudness: value.loudness,
            at: Date.now(),
            bytes: value.buffer.length,
        }))
        await fs.rename(tmpBody, paths.body)
        await fs.rename(tmpMeta, paths.meta)
        await cleanupDiskCache()
    } catch (error) {
        await fs.unlink(tmpBody).catch(() => {})
        await fs.unlink(tmpMeta).catch(() => {})
        console.warn('[QishuiAudio] 磁盘缓存写入失败:', error?.message || error)
    }
}

const remember = (key, value, { persistDisk = true } = {}) => {
    const existing = cache.get(key)
    if (existing) cacheBytes -= existing.buffer.length
    cache.set(key, { ...value, at: Date.now() })
    cacheBytes += value.buffer.length
    while (cacheBytes > MAX_CACHE_BYTES && cache.size > 1) {
        const oldest = [...cache.entries()].sort((a, b) => a[1].at - b[1].at)[0]
        cache.delete(oldest[0])
        cacheBytes -= oldest[1].buffer.length
    }
    if (persistDisk) void writeDiskCache(key, value)
}

const cacheKey = (url, auth) => crypto.createHash('sha1').update(`${url}\n${auth}`).digest('hex')
const etagForKey = (key) => `"${key}"`

const PLAY_TOKEN_TTL = 30 * 60 * 1000
const playTokens = new Map()

const prunePlayTokens = () => {
    const now = Date.now()
    for (const [token, entry] of playTokens) {
        if (now > entry.expiresAt) playTokens.delete(token)
    }
    if (playTokens.size <= 1000) return
    const oldest = [...playTokens.entries()].sort((a, b) => a[1].expiresAt - b[1].expiresAt)
    for (let index = 0; index < oldest.length - 1000; index += 1) {
        playTokens.delete(oldest[index][0])
    }
}

export const createQishuiPlayToken = (url, auth, mimeType) => {
    validateQishuiAudioUrl(url)
    const token = crypto.randomBytes(16).toString('hex')
    playTokens.set(token, {
        url,
        auth,
        mimeType: mimeType || '',
        expiresAt: Date.now() + PLAY_TOKEN_TTL,
    })
    prunePlayTokens()
    preloadQishuiAudio(url, auth)
    return token
}

export const resolveQishuiPlayToken = (token) => {
    const entry = playTokens.get(String(token || ''))
    if (!entry) return null
    if (Date.now() > entry.expiresAt) {
        playTokens.delete(String(token))
        return null
    }
    return entry
}

export const isQishuiRequestAbort = (error, signal) => {
    if (signal?.aborted) return true
    return error?.name === 'AbortError' || error?.code === 'ABORT_ERR'
        || /^(?:terminated|aborted)$/i.test(String(error?.message || '').trim())
        || /(?:client|request|socket).*(?:closed|abort|reset)|aborted/i.test(String(error?.message || ''))
}

const fetchQishuiSource = async (url, signal) => {
    try {
        return await fetch(url, {
            signal,
            redirect: 'error',
            headers: { 'User-Agent': WEB_UA, Referer: 'https://www.qishui.com/' },
        })
    } catch (error) {
        if (signal?.aborted || error?.name === 'AbortError') throw error
        if (!/terminated|socket|reset|network/i.test(String(error?.message || ''))) throw error
        return fetch(url, {
            signal,
            redirect: 'error',
            headers: { 'User-Agent': WEB_UA, Referer: 'https://www.qishui.com/' },
        })
    }
}

export const preloadQishuiAudio = (url, auth) => {
    void loadQishuiAudio(url, auth).catch(error => {
        if (!isQishuiRequestAbort(error)) {
            console.warn('[QishuiAudio] 预热失败')
        }
    })
}

const ensureQishuiLoudness = async (value) => {
    // 响度分析暂时停用：避免为播放链路额外下载与解码整首音频。
    return value
}

export const loadQishuiAudio = async (url, auth) => {
    validateQishuiAudioUrl(url)
    const key = cacheKey(url, auth)
    const cached = cache.get(key)
    if (cached) {
        cached.at = Date.now()
        const enriched = await ensureQishuiLoudness(cached)
        if (enriched !== cached) remember(key, enriched)
        return { ...enriched, cacheHit: true, cacheSource: 'memory' }
    }

    const diskCached = await readDiskCache(key)
    if (diskCached) {
        const enriched = await ensureQishuiLoudness(diskCached)
        remember(key, enriched, { persistDisk: Boolean(enriched.loudness) })
        return { ...enriched, cacheHit: true, cacheSource: 'disk' }
    }

    if (inflightLoads.has(key)) {
        const shared = await inflightLoads.get(key)
        return { ...shared, cacheHit: shared.cacheHit ?? false, cacheSource: shared.cacheSource || 'shared' }
    }

    // 与客户端 Range/切歌断开解耦：后台拉完并缓存，避免低带宽服务器重复从 CDN 下载。
    const task = (async () => {
        const response = await fetchQishuiSource(url, AbortSignal.timeout(60_000))
        if (!response.ok) throw new Error(`汽水音频下载失败: ${response.status}`)
        const reader = response.body.getReader()
        const chunks = []
        let size = 0
        try {
            while (true) {
                const { done, value } = await reader.read()
                if (done) break
                size += value.byteLength
                if (size > MAX_SOURCE_BYTES) throw new Error('汽水音频超过大小限制')
                chunks.push(value)
            }
        } finally {
            await reader.cancel().catch(() => {})
        }
        const raw = Buffer.concat(chunks, size)
        const result = auth ? decryptQishuiAudio(raw, auth) : {
            buffer: raw,
            contentType: response.headers.get('content-type') || 'audio/mp4',
        }
        const enriched = await ensureQishuiLoudness(result)
        remember(key, enriched)
        return { ...enriched, cacheHit: false, cacheSource: 'cdn' }
    })()

    inflightLoads.set(key, task)
    try {
        return await task
    } finally {
        inflightLoads.delete(key)
    }
}

/** 汽水解密地址，播放 token 有效期 30 分钟，仍需 OpenMusic 服务端签名 */
export const buildQishuiAudioUrl = (ctx, { url, auth, mimeType }) => {
    const token = createQishuiPlayToken(url, auth, mimeType)
    const endpoint = new URL(`${get_public_base(ctx)}/audio/qishui`)
    endpoint.searchParams.set('t', token)
    endpoint.searchParams.set('k', cacheKey(url, auth))
    if (mimeType) endpoint.searchParams.set('mime_type', mimeType)
    return endpoint.toString()
}

export const wrapQishuiPlayPayload = (ctx, payload) => {
    if (!payload?.url || !payload?.auth) return payload
    const wrapped = { ...payload, url: buildQishuiAudioUrl(ctx, payload) }
    delete wrapped.auth
    return wrapped
}

const CHUNK_SIZE = 512 * 1024

const bodyForRange = (buffer, start, end) => {
    const slice = buffer.subarray(start, end + 1)
    if (slice.length <= CHUNK_SIZE) return slice
    return new ReadableStream({
        start(controller) {
            let offset = 0
            while (offset < slice.length) {
                const next = slice.subarray(offset, offset + CHUNK_SIZE)
                offset += next.length
                controller.enqueue(next)
            }
            controller.close()
        },
    })
}

export const qishuiAudioResponse = async (ctx) => {
    const token = String(ctx.req.query('t') || '').trim()
    if (!token) return ctx.json({ error: 'missing qishui play token' }, 400)

    const entry = resolveQishuiPlayToken(token)
    if (!entry) return ctx.json({ error: 'qishui play token expired' }, 403)

    const url = entry.url
    const auth = entry.auth
    validateQishuiAudioUrl(url)

    const mode = String(ctx.req.query('mode') || '').trim().toLowerCase()

    // OpenMusic 只需要源地址和密钥，随后直接从汽水 CDN 下载，避免音频再次经过 Meting 中转。
    if (mode === 'source') {
        return ctx.json({
            url,
            auth,
            mimeType: entry.mimeType || 'audio/mp4',
        })
    }

    const signal = ctx.req.raw?.signal
    const contentKey = cacheKey(url, auth)
    const etag = etagForKey(contentKey)
    if (ctx.req.header('if-none-match') === etag) {
        return new Response(null, {
            status: 304,
            headers: {
                ETag: etag,
                'Cache-Control': `private, max-age=${Math.floor(PLAY_TOKEN_TTL / 1000)}`,
            },
        })
    }
    const audio = await loadQishuiAudio(url, auth)
    if (signal?.aborted) throw Object.assign(new Error('汽水音频请求已取消'), { name: 'AbortError' })
    const total = audio.buffer.length
    const match = /^bytes=(\d*)-(\d*)$/i.exec(ctx.req.header('range') || '')
    let start = 0
    let end = total - 1
    let status = 200
    if (match) {
        start = match[1] ? Number(match[1]) : 0
        end = match[2] ? Number(match[2]) : total - 1
        if (!Number.isFinite(start) || !Number.isFinite(end) || start < 0 || start > end || start >= total) {
            return new Response(null, { status: 416, headers: { 'Content-Range': `bytes */${total}` } })
        }
        end = Math.min(end, total - 1)
        status = 206
    }
    const headers = {
        'Content-Type': audio.contentType,
        'Accept-Ranges': 'bytes',
        'Access-Control-Allow-Origin': '*',
        'Cache-Control': `private, max-age=${Math.floor(PLAY_TOKEN_TTL / 1000)}`,
        ETag: etag,
        'X-Accel-Buffering': 'no',
        'X-Qishui-Cache': audio.cacheHit ? (audio.cacheSource || 'hit') : 'miss',
    }
    if (status === 206) {
        headers['Content-Range'] = `bytes ${start}-${end}/${total}`
        headers['Content-Length'] = String(end - start + 1)
    } else {
        headers['Content-Length'] = String(total)
    }
    return new Response(bodyForRange(audio.buffer, start, end), { status, headers })
}
