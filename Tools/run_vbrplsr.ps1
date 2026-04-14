Set-Location 'c:\Users\eller\Desktop\PULSAR'
$input = 'TestWAVs\Strike A Pose! 30s.wav'
$output = 'TestWAVs\Output\Strike A Pose! 30s vbr9.plsr'
$decoded = 'TestWAVs\Output\Strike A Pose! 30s vbr9.decoded.wav'
dotnet .\bin\Debug\net8.0\PulsarCodec.dll --vbrplsr -V 9 --vbr $input $output $decoded
