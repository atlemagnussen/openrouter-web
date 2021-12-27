export const formatterNoDateAndTimeHardCode = (ds: Date | string): string => {
    const date = new Date(ds)
    return `${date.getFullYear()}-${date.getMonth()}-${date.getDate()} ${date.getHours()}:${date.getMinutes()}:${date.getSeconds()}`
}
export const formatterNoDateAndTime = Intl.DateTimeFormat("nb-NO", {
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit'
})
export const formatterNoDateOnly = Intl.DateTimeFormat("nb-NO", {
    year: 'numeric',
    month: '2-digit',
    day: '2-digit'
})