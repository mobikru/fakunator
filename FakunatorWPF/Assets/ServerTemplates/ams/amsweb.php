<?php
//-------- Пароль доступа к статистике ------
// В целях безопасности настроятельно рекомендуем задать пароль доступка в следующей строчке.
// Этот пароль нужно будет так же указать в AMS в разделе Настройки->RealTime статистика при
// добавлении ссылки на данный скрипт.

$Password = "__AMSPASS__";

// ----------------------------------------------------------------------------
// Заголовок Html
// ----------------------------------------------------------------------------
$HtmlHead = '<html><head><META content="text/html; charset=utf-8" http-equiv=Content-Type><title>Processing data</title></head>';

// ----------------------------------------------------------------------------
// Сообщения форм
// ----------------------------------------------------------------------------
// В следующих строках можно задать сообщения, которые будут показаны скриптом после отправки
// формы подписки/отписки и кликов на ссылки-подтверждения.

$Form_Submit_Error       = "Введен не корректный E-Mail адрес<br>Пожалуйста, введите правильный E-Mail для успешной отправки формы";
$Form_Submit_OK          = "Спасибо.<br>Ваш запрос обрабатывается.<br>Вы получите запрос на подтверждение подписки на указанный e-mail";
$Confirmation_Link_Click = "Подтверждение получено, спасибо !";
$Unsubscribe_Link_Click  = "Спасибо, Вы были успешно отписаны от нашей рассылки !";

// ----------------------------------------------------------------------------
// Проверка на ботов (что открытие письма или клик на ссылку делает бот)
// ----------------------------------------------------------------------------
$userAgentUrl                  = '';  // url на текстовый файл с user-agent'ами (по одному в строке), которые должны блокироваться
$ipBlackListUrl                = '';  // url на текстовый файл с IP (по одному в строке), которые должны блокироваться
$blackListFilesRefreshInterval = 60;  // Интервал загрузки обновленных данных с урлов выше, в минутах.

// ----------------------------------------------------------------------------
// Не вносите изменений в скрипт ниже этой строки.
// ----------------------------------------------------------------------------
$Form_Email    = 'no_email';
$FormName      = 'no_form';
$Form_FullName = 'no_form_fullname';

// ----------------------------------------------------------------------------
// Start
// ----------------------------------------------------------------------------

if(isset($_GET["GetIPInfo"]))
{
    echo $_SERVER['REMOTE_ADDR']."=".gethostbyaddr($_SERVER['REMOTE_ADDR']);
    exit;
}

$ClientUserAgent = @$_SERVER['HTTP_USER_AGENT'];
if ($userAgentUrl != '')
{
    $userAgentFileName = 'bluseragents.txt';
    if (refreshFileFromUrl($userAgentFileName, $userAgentUrl, $blackListFilesRefreshInterval))
    {
        if (fileContainsLine($userAgentFileName, $ClientUserAgent))
            exit();
    }
}

if(isset($_SERVER['HTTP_CLIENT_IP']))
   $ClientIp =  $_SERVER['HTTP_CLIENT_IP'];
else if(isset($_SERVER['HTTP_X_FORWARDED_FOR']))
   $ClientIp = $_SERVER['HTTP_X_FORWARDED_FOR'];	 
else
   $ClientIp = $_SERVER['REMOTE_ADDR'];

if ($ipBlackListUrl != '')
{
    $ipBlackListFileName = 'blips.txt';
    
    if (refreshFileFromUrl($ipBlackListFileName, $ipBlackListUrl, $blackListFilesRefreshInterval))
    {
        if (fileContainsLine($ipBlackListFileName, $ClientIp))
            exit();
    }
}

if (empty($ClientIp))
    $ClientIp = "no_ip";

if (empty($ClientUserAgent))
    $ClientUserAgent = "no_agent";

if (count($_GET) === 0 && count($_POST) === 0)
{
    // check script installation and required modules
    CheckScriptInstallation();
    exit;
}

if (isset($_POST['FormID']) && isset($_POST['FormProgID']))
{
    // check is subscribe/unsubscribe form submitted
    $FormName = trim($_POST['FormID']);
    
    $ProgID = trim(@$_POST['FormProgID']);
    
    if ($ProgID == '' || !is_numeric(substr($ProgID, 4)))
    {
        echo "Unable to decode URL !";
        exit;
    }
    
    
    $Form_Email = strtolower(trim(@$_POST['FormEmail']));
    
    if (!filter_var($Form_Email, FILTER_VALIDATE_EMAIL))
    {
        echoHTML($Form_Submit_Error);
        exit;
    }
    
    if (isset($_POST['FormFullName']) && !empty($_POST['FormFullName']))
    {
        $Form_FullName = trim($_POST['FormFullName']);
        $Form_FullName = preg_replace_callback("/(&#[0-9]+;)/", 'replaceCall', $Form_FullName);
    }
    
    //echoHTML($Form_Submit_OK);
    writeLog("Form_Data=$FormName:$Form_FullName:$Form_Email{|;");
	header("Location: http://куда-вам-надо");
    exit;
}

// Traceable link click/opened message counter/(un)subscribe conformation click/receive statistic command(s)
$InRequest = trim($_SERVER['QUERY_STRING']);

// Decrypt request
if (version_compare(phpversion(), "5.5.0", "<"))
    $InRequest = mcrypt_cbc(MCRYPT_RIJNDAEL_128, $Password, base64_decode(urldecode($InRequest)), MCRYPT_DECRYPT, 'amsstatinivector');
else if (version_compare(phpversion(), "7.0.0", "<"))
{
    $td = mcrypt_module_open(MCRYPT_RIJNDAEL_128, '', 'cbc', '');
    mcrypt_generic_init($td, $Password, 'amsstatinivector');
    $InRequest = mdecrypt_generic($td, base64_decode(urldecode($InRequest)));
    mcrypt_generic_deinit($td);
    mcrypt_module_close($td);
}
else if (extension_loaded('openssl'))
    $InRequest = openssl_decrypt(base64_decode(urldecode($InRequest)), 'AES-128-CBC', $Password, OPENSSL_RAW_DATA | OPENSSL_ZERO_PADDING, 'amsstatinivector');
else
{
    echo "Can't decrypt query: unable to initialize mcrypt or OpenSSL cryptographic extensions";
    exit;
}

// Process commands
if (strpos($InRequest, 'amsclk') !== false)
{
    // Traceable link click/opened message counter/(un)subscribe confimation click
    $InRequest = substr($InRequest, 7);
    
    if (!mb_detect_encoding($InRequest, 'ASCII', true))
    {
        echo 'Unable to decode URL ! (1)';
        exit;
    }
    
    $ParamsArray = parseRequest($InRequest);
    
    // ProgID
    if (isset($ParamsArray['PID']))
    {
        $ProgID = $ParamsArray['PID'];
        
        $pidAms = strpos($ProgID, 'AMS_') !== false;
        $pidMpc = strpos($ProgID, 'MPC_') !== false;
        
        if ((!$pidAms && !$pidMpc) || !is_numeric(substr($ProgID, 4)))
        {
            echo 'Unable to decode URL ! (2)';
            exit;
        }
        
        getRequestParam($ParamsArray, 'GID', $GroupID, '-1', 3, true);
        getRequestParam($ParamsArray, 'MLID', $MailingID, '-1', 4, true);
        getRequestParam($ParamsArray, 'MSID', $MessageID, '-1', 5, true);
        getRequestParam($ParamsArray, 'CNTID', $ContactID, '-1', 6, true);
        getRequestParam($ParamsArray, 'SID', $StartID, '-1', 7, true);
        getRequestParam($ParamsArray, 'EML', $RcptEmail, 'no_email', 8, false);
        getRequestParam($ParamsArray, 'RD', $RedirURL, 'nourl', 9, false);
        
        // Write open/click data to temporary log
        if ($pidAms)
        {
            if (isset($ParamsArray['UAction']))
            {
				$UnsubscribeAction = isset($ParamsArray['UAction']) ? $ParamsArray['UAction'] : 'no_action';
					writeLog("$MailingID:$GroupID:$StartID:$ContactID:$RcptEmail:Unsubscribe_Click:$UnsubscribeAction{|;");
				if($RedirURL == 'un_clk')
					echoHTML($Unsubscribe_Link_Click);
				else	
					header("Location: $RedirURL");	
            }
            else
            {
                writeLog("$MailingID:$GroupID:$StartID:$ContactID:$RcptEmail:$RedirURL{|;");
            }
        }
        else if ($pidMpc)
        {
            if ($FormName == 'no_form')
            {
                if ($RedirURL == 'nourl')
                    echoHTML($Confirmation_Link_Click);
                
                writeLog("Confirm_Data=$MessageID{|;");
            }
            else // maxter, мы тут только если это был клик на ссылку подтверждения подписки. если это не так то выдаем ошибку
            {
                echo 'Unable to decode URL ! (3)';
                exit;
            }
        }
        
        // Redirect or return image
        if ($RedirURL != 'nourl')
        {
            if ($RedirURL == 'open_trace')
            {
                header('Content-type: image/png');
                header('Content-length: 95');
                echo "\x89\x50\x4e\x47\x0d\x0a\x1a\x0a\x00\x00\x00\x0d\x49\x48\x44\x52\x00\x00\x00\x01\x00\x00\x00\x01\x01\x03\x00\x00\x00\x25\xdb\x56\xca\x00\x00\x00\x03\x50\x4c\x54\x45\x00\x00\x00\xa7\x7a\x3d\xda\x00\x00\x00\x01\x74\x52\x4e\x53\x00\x40\xe6\xd8\x66\x00\x00\x00\x0a\x49\x44\x41\x54\x08\xd7\x63\x60\x00\x00\x00\x02\x00\x01\xe2\x21\xbc\x33\x00\x00\x00\x00\x49\x45\x4e\x44\xae\x42\x60\x82";
            }
            else
            {
                if ((strpos(strtolower($RedirURL), 'http://') === 0) || (strpos(strtolower($RedirURL), 'https://') === 0))
                {
                    header("Location: $RedirURL");
                }
            }
        }
    }
    exit;
}
else if (strpos($InRequest, 'amscmd') !== false) // MakeCopy/GetCopy commands
{
    $InRequest = substr($InRequest, 6);
    
    $ParamsArray = parseRequest($InRequest);
    if (isset($ParamsArray['PID']))
    {
        $ProgID = $ParamsArray['PID'];
        
        if (isset($ParamsArray['MCPY']))
            makeCopy();
        if (isset($ParamsArray['GCPY']))
            getCopy();
    }
}
else
    echo 'Unknown request type';
// ----------------------------------------------------------------------------

// ----------------------------------------------------------------------------
// Check is script corrctly installed and all needed PHP extension available
// ----------------------------------------------------------------------------
function CheckScriptInstallation()
{
    $CheckRes = true;
    global $HtmlHead;
    echo $HtmlHead;
    echo "Проверка установки и работоспособности скрипта...<br><br>";
    echo "Версия PHP: " . phpversion() . "<br><br>";
    echo "Расширение mbstring: ";
    if (extension_loaded("mbstring"))
        echo "OK<br>";
    else
    {
        echo "Не установлено ! Требуется включить расширение mbstring в настройках РHP или в личном кабинете хостера<br>";
        $CheckRes = false;
    }
    if (version_compare(phpversion(), "7.0.0", "<"))
    {
        echo "Расширение mcrypt: ";
        if (extension_loaded("mcrypt"))
            echo "OK<br>";
        else
        {
            echo "не установлено ! Требуется включить расширение mcrypt в настройках РHP или в личном кабинете хостера<br>";
            $CheckRes = false;
        }
        if (version_compare(phpversion(), "5.5.0", "<"))
        {
            echo "Функция mcrypt_cbc: ";
            if (function_exists("mcrypt_cbc"))
                echo "OK<br>";
            else
            {
                echo "Не найдена ! Проверьте настройки PHP: требуется расширение mcrypt для поддержки криптографии<br>";
                $CheckRes = false;
            }
        }
        else
        {
            echo "Функция mcrypt_cbc: ";
            if (function_exists("mdecrypt_generic"))
                echo "OK<br>";
            else
            {
                echo "Не найдена ! Проверьте настройки PHP: требуется расширение mcrypt для поддержки криптографии<br>";
                $CheckRes = false;
            }
        }
    }
    else
    {
        echo "Расширение openssl: ";
        if (extension_loaded("openssl"))
            echo "OK<br>";
        else
        {
            echo "не установлено ! Требуется включить расширение openssl в настройках РHP или в личном кабинете хостера<br>";
            $CheckRes = false;
        }
    }
    echo "Функция mb_detect_encoding: ";
    if (function_exists("mb_detect_encoding"))
        echo "OK<br>";
    else
    {
        echo "Не найдена ! Проверьте настройки PHP: требуется расширение mbstring !<br>";
        $CheckRes = false;
    }
    echo "<br>";
    echo "Пытаемся создать файл test.log... ";
    $TestFile = fopen('test.log', 'w');
    if ($TestFile)
    {
        echo "OK<br>";
        echo "Пытаемся записать данные в файл...";
        if (fwrite($TestFile, "this is a test") === false)
        {
            echo "Не удача ! Проверьте, что для папки, в которой расположен скрипт, а так же для самого файла скрипта назначены права доступа 755 !<br>";
            $CheckRes = false;
        }
        else
            echo "OK<br>";
        fclose($TestFile);
        echo "Пытаемся скопировать test.log -> test.out...";
        if (copy('test.log', 'test.out') === false)
        {
            echo "Не удача ! Проверьте, что для папки, в которой расположен скрипт, а так же для самого файла скрипта назначены права доступа 755 !<br>";
            $CheckRes = false;
        }
        else
            echo "OK<br>";
    }
    else
    {
        echo "Не удача ! Проверьте, что для папки, в которой расположен скрипт, а так же для самого файла скрипта назначены права доступа 755 !<br>";
        $CheckRes = false;
    }
    if (file_exists('test.log'))
        unlink('test.log');
    if (file_exists('test.out'))
        unlink('test.out');
    echo "<br>";
    if ($CheckRes == true)
        echo "Проверка прошла успешно, скрипт установлен и функционирует правильно !<br>";
    else
        echo "В процессе проверки возникли ошибки. Необходимо их исправить прежде чем скрипт будет готов к использованию !";
    echo "</HTML>";
}
// ----------------------------------------------------------------------------

// ----------------------------------------------------------------------------
// Utility functions
// ----------------------------------------------------------------------------
function makeCopy()
{
    global $ProgID;
    
    $logFileName = $ProgID . '.log';
    $outFileName = $ProgID . '.out';
    
    
    if (!file_exists($logFileName))
    {
        echo 'Error: No File';
        exit;
    }
    
    if (!copy($logFileName, $outFileName))
    {
        echo 'Error: Can\'t create output file. Permission denied.';
        exit;
    }
    
    chmod($outFileName, 0777);
    
    $LogFile = fopen($logFileName, 'w');
    if (!$LogFile)
    {
        echo 'Error: Can\'t update input file. Permission denied.';
        exit;
    }
    
    flock($LogFile, LOCK_EX);
    ftruncate($LogFile, 0);
    flock($LogFile, LOCK_UN);
    fclose($LogFile);
    
    chmod($logFileName, 0777);
    
    echo 'cmd_ok';
    
    exit;
}

// ----------------------------------------------------------------------------

function getCopy()
{
    global $ProgID;
    
    $outFileName = $ProgID . '.out';
    
    if (!file_exists($outFileName))
    {
        echo 'Error: No File';
        exit;
    }
    
    $LogFile = fopen($outFileName, 'r');
    if (!$LogFile)
    {
        echo 'Error: Can\'t open out file';
        exit;
    }
    
    flock($LogFile, LOCK_EX);
    while (!feof($LogFile))
    {
        $Buffer = fgets($LogFile, 4096);
        echo $Buffer;
    }
    flock($LogFile, LOCK_UN);
    fclose($LogFile);
    
    echo 'cmd_ok';
    
    exit;
}

// ----------------------------------------------------------------------------

function writeLog($string)
{
    global $ProgID;
    global $ClientIp;
    global $ClientUserAgent;
    
    $logFileName = $ProgID . '.log';
    
    $LogFile = fopen($logFileName, 'ab');
    if (!$LogFile)
    {
        echo 'Error: Can\'t open log file. Permission denied.';
        exit;
    }
    
    flock($LogFile, LOCK_EX);
    fwrite($LogFile, $string . base64_encode($ClientIp) . '{|;' . base64_encode($ClientUserAgent) . '{|;' . $_SERVER['REQUEST_TIME'] . "\r\n");
    fflush($LogFile);
    flock($LogFile, LOCK_UN);
    fclose($LogFile);
    chmod($logFileName, 0777);
}

// ----------------------------------------------------------------------------

function echoHTML($text)
{
    global $HtmlHead;
    
    header('Content-Type: text/html; charset=utf-8');
    echo $HtmlHead . '<body style="background: #E6E6E6; text-align: center; color: #003399">' . $text . '</body></html>';
}

// ----------------------------------------------------------------------------

function parseRequest($request)
{
    $result = array();
    
    $params = explode('|{', $request);
    foreach ($params as $param)
    {
        if (strpos($param, '=') !== false)
        {
            list($p, $v) = explode('=', $param,2); 
            $result[trim($p)] = trim($v);
        }
        else
        {
            $result[trim($param)] = '';
        }
    }
    
    return $result;
}

// ----------------------------------------------------------------------------

function getRequestParam($params, $name, &$value, $defaultValue, $errorIndex, $isNumeric)
{
    if (isset($params[$name]))
    {
        $value = $params[$name];
        if ($isNumeric && !is_numeric($value))
        {
            echo "Unable to decode URL ! ($errorIndex)";
            exit;
        }
    }
    else
        $value = $defaultValue;
}

// ----------------------------------------------------------------------------

function replaceCall($matches)
{
    return mb_convert_encoding($matches[1], 'UTF-8', 'HTML-ENTITIES');
}
// ----------------------------------------------------------------------------

function refreshFileFromUrl($fileName, $url, $intervalInMinutes)
{
    if (file_exists($fileName) && (time() < (filemtime($fileName) + $intervalInMinutes * 60)))
        return true;
    
    $content = @file_get_contents($url);
    if ($content === false)
        return false;
    
    return @file_put_contents($fileName, $content) !== false;
}

// ----------------------------------------------------------------------------

function fileContainsLine($fileName, $line)
{
    $fd = fopen($fileName, "r");
    while (!feof($fd))
    {
        if (trim(fgets($fd, 16384)) == $line)
            return true;
    }
    fclose($fd);
    
    return false;
}
// ----------------------------------------------------------------------------
?>