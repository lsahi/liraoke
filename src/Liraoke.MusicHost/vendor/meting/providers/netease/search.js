import { request } from "./util.js"
import { map_song_list } from "./util.js"

export const get_search_songs = async (id, cookie = '', { limit = 30, offset = 0 } = {}) => {

    const data = {
        s: id,
        type: 1,
        limit,
        offset,
        total: true,
    }

    const res = await request('POST', `https://interface.music.163.com/eapi/cloudsearch`, data, {
        crypto: 'eapi',
        cookie: cookie || {},
        url: '/api/cloudsearch/pc'
    })

    if (res.code && Number(res.code) !== 200) throw new Error(res.message || '网易云歌曲搜索失败')
    return map_song_list({ songs: res.result?.songs || [] })

}
